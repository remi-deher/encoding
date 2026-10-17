using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Api.Flows;
using System.Text.Json;
namespace Encoding.Api.Executions;

/// <summary>Persistent lifecycle journal. Does not assign work or publish media.</summary>
public sealed class ExecutionRepository(DbDataSource dataSource, FlowComponentRegistry? components = null)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private const string Columns = """
 e.id,e.media,e.library_name,e.flow_name,e.flow_version,e.state,e.node_id,n.name,
 e.progress,e.detail,e.attempt,e.revision,e.queued_at,e.started_at,e.finished_at,e.updated_at,w.before_metadata::text,w.after_metadata::text,w.published_path
 ,(SELECT checkpoint::text FROM execution_steps s WHERE s.execution_id=e.id AND s.completed_at IS NOT NULL ORDER BY s.completed_revision DESC LIMIT 1),w.library_id
 """;
    private static ExecutionResponse Read(DbDataReader r) => new(
    r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5),
    r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetDecimal(8),
    r.GetString(9), r.GetInt32(10), r.GetInt32(11), r.GetFieldValue<DateTimeOffset>(12),
    r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14), r.GetFieldValue<DateTimeOffset>(15),ReadMetadata(r,16),ReadMetadata(r,17),r.IsDBNull(18)?null:r.GetString(18),ReadVideoEncodings(r,19),r.IsDBNull(20)?null:r.GetGuid(20));
    private static System.Text.Json.JsonElement? ReadMetadata(DbDataReader reader,int index) => reader.IsDBNull(index)?null:System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(reader.GetString(index));
    private static IReadOnlyList<ResolvedVideoEncoding> ReadVideoEncodings(DbDataReader reader,int index)
    {
        if(reader.IsDBNull(index)) return [];
        try
        {
            var checkpoint=JsonSerializer.Deserialize<RuntimeCheckpoint>(reader.GetString(index),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return checkpoint?.Context.VideoEncodings??[];
        }
        catch(JsonException) { return []; }
    }
    public async Task<ExecutionOverviewResponse> OverviewAsync(CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var counts = new Dictionary<string, int>();
        await using (var cmd = Sql.Command("SELECT state,count(*)::integer FROM executions GROUP BY state", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) counts[r.GetString(0)] = r.GetInt32(1); }
        var list = new List<ExecutionResponse>();
        await using (var cmd = Sql.Command($"SELECT {Columns} FROM executions e LEFT JOIN cluster_nodes n ON n.id=e.node_id LEFT JOIN execution_work w ON w.execution_id=e.id ORDER BY CASE WHEN e.state IN ('queued','running','cancelling') THEN 0 ELSE 1 END,e.updated_at DESC,e.id LIMIT 200", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) list.Add(Read(r)); }
        var activity = new List<ExecutionActivityResponse>();
        await using (var cmd = Sql.Command("SELECT v.id,v.execution_id,e.media,v.state,v.detail,v.created_at FROM execution_events v JOIN executions e ON e.id=v.execution_id ORDER BY v.created_at DESC,v.id LIMIT 20", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) activity.Add(new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5))); }
        await tx.CommitAsync(ct);
        int Count(string s) => counts.GetValueOrDefault(s);
        return new(list, Count("queued"), Count("running") + Count("cancelling"), Count("failed"), Count("cancelled"), Count("succeeded"), DateTimeOffset.UtcNow, activity, Count("deferred"));
    }

    public async Task<ExecutionResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command($"SELECT {Columns} FROM executions e LEFT JOIN cluster_nodes n ON n.id=e.node_id LEFT JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id", c);
        cmd.Parameters.AddWithValue("id", id); await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }
    public async Task<IReadOnlyList<ExecutionEventResponse>> EventsAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("SELECT id,revision,state,detail,created_at FROM execution_events WHERE execution_id=@id ORDER BY revision", c);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct); var list = new List<ExecutionEventResponse>();
        while (await r.ReadAsync(ct)) list.Add(new(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }
    public async Task<IReadOnlyList<ExecutionPipelineStepResponse>> PipelineAsync(Guid id,CancellationToken ct)
    {
        FlowDefinition flow;
        string executionState;
        await using (var execution=dataSource.CreateCommand("SELECT flow_definition::text,state FROM executions WHERE id=@id"))
        {
            execution.Parameters.AddWithValue("id",id);
            await using var reader=await execution.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct)) return [];
            flow=JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),Json)!;
            executionState=reader.GetString(1);
        }
        var flowNodes=flow.Nodes.ToDictionary(node=>node.Id,StringComparer.Ordinal);
        await using var cmd=dataSource.CreateCommand("""
          SELECT s.node_id,s.invocation_id,s.started_at,s.completed_at,s.checkpoint::text,s.command_logs::text
          FROM execution_steps s WHERE s.execution_id=@id ORDER BY s.started_at,s.node_id
          """);
        cmd.Parameters.AddWithValue("id",id); await using var r=await cmd.ExecuteReaderAsync(ct);
        var result=new List<ExecutionPipelineStepResponse>();
        while(await r.ReadAsync(ct))
        {
            var nodeId=r.GetString(0);
            var node=flowNodes.GetValueOrDefault(nodeId);
            var descriptor=node is null?null:components?.Resolve(node.Component);
            DateTimeOffset? completedAt=r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3);
            var checkpoint=r.IsDBNull(4)?null:JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(4),Json);
            var outputId=checkpoint?.OutputId;
            var outputLabel=outputId is null?null:descriptor?.Outputs.FirstOrDefault(output=>output.Id==outputId)?.Label??outputId;
            var status=completedAt is not null?"completed":executionState switch
            {
                "failed"=>"failed",
                "cancelled"=>"cancelled",
                "cancelling"=>"cancelling",
                _=>"running"
            };
            var logs=JsonSerializer.Deserialize<IReadOnlyList<RuntimeCommandLog>>(r.GetString(5),new JsonSerializerOptions(JsonSerializerDefaults.Web))??[];
            result.Add(new(nodeId,node?.Name??descriptor?.Name??nodeId,r.GetGuid(1),descriptor?.Kind??"unknown",status,
                r.GetFieldValue<DateTimeOffset>(2),completedAt,outputId,outputLabel,checkpoint?.NextNodeId,logs));
        }
        return result;
    }
    internal static async Task<bool> CreateInTransactionAsync(CreateExecutionRequest request,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd = Sql.Command("""
   INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,detail)
   VALUES(@id,@media,@library,@flow,@version,@definition,'queued',1,'En attente du moteur d’exécution.') ON CONFLICT(id) DO NOTHING
   """, c, tx);
        cmd.Parameters.AddWithValue("id", request.Id); cmd.Parameters.AddWithValue("media", request.Media);
        cmd.Parameters.AddWithValue("library", request.Library); cmd.Parameters.AddWithValue("flow", request.Flow);
        cmd.Parameters.AddWithValue("version", request.FlowVersion); cmd.Parameters.AddWithValue("definition", SqlKind.Json, request.FlowDefinition.GetRawText());
        var inserted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (inserted) await AddEvent(c, tx, request.Id, Guid.NewGuid(), 0, "queued", "En attente du moteur d’exécution.", ct);
        return inserted;
    }
    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = Sql.Command("""
   UPDATE executions SET state=CASE WHEN state='queued' THEN 'cancelled' ELSE 'cancelling' END,
   detail=CASE WHEN state='queued' THEN 'Annulée avant démarrage.' ELSE 'Annulation demandée ; attente de confirmation du worker.' END,
   finished_at=CASE WHEN state='queued' THEN clock_timestamp() ELSE NULL END,
   updated_at=clock_timestamp(),revision=revision+1 WHERE id=@id AND state IN ('queued','running')
   RETURNING revision,state,detail
   """, c, tx); cmd.Parameters.AddWithValue("id", id);
        int revision; string state, detail;
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) return false; revision = r.GetInt32(0); state = r.GetString(1); detail = r.GetString(2); }
        await AddEvent(c, tx, id, Guid.NewGuid(), revision, state, detail, ct); await tx.CommitAsync(ct); return true;
    }
    public async Task<Guid?> RetryAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var created=await RetryInTransactionAsync(id,null,null,"Nouvelle tentative demandée.",c,tx,ct);
        if(created is null) return null;
        await tx.CommitAsync(ct); return created;
    }

    internal static async Task<Guid?> RetryInTransactionAsync(Guid id,RuntimeHandoffSelector? selector,Guid? previousWorker,string detail,
        DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var next=Guid.NewGuid();
        await using(var cmd=Sql.Command("""
          INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,retry_of,detail)
          SELECT @next,media,library_name,flow_name,flow_version,flow_definition,'queued',attempt+1,id,@detail
          FROM executions WHERE id=@id AND state='failed' ON CONFLICT(retry_of) DO NOTHING RETURNING id
          """,c,tx))
        {
            cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("next",next);cmd.Parameters.AddWithValue("detail",detail);
            if(await cmd.ScalarGuidAsync(ct) is not {} created) return null;
            next=created;
        }
        await AddEvent(c,tx,next,Guid.NewGuid(),0,"queued",detail,ct);
        await using(var work=Sql.Command("""
          INSERT INTO execution_work(execution_id,library_id,media_path,media_key,handoff_selector,previous_worker_node_id)
          SELECT @next,library_id,media_path,media_key,@selector,@previous FROM execution_work WHERE execution_id=@id
          """,c,tx))
        {
            work.Parameters.AddWithValue("next",next);work.Parameters.AddWithValue("id",id);
            work.Parameters.AddWithValue("selector",SqlKind.Json,selector is null?DBNull.Value:JsonSerializer.Serialize(selector,Json));
            work.Parameters.AddWithValue("previous",SqlKind.Uuid,(object?)previousWorker??DBNull.Value);
            if(await work.ExecuteNonQueryAsync(ct)==0) throw new InvalidOperationException("L’exécution d’origine ne possède pas de contexte de média à relancer.");
        }
        return next;
    }
    // Called by the future orchestrator after its lease and output validation checks.
    public async Task<bool> RecordAsync(Guid id, RecordExecutionProgress update, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var recorded=await RecordInTransactionAsync(id,update,c,tx,ct);
        if(recorded) await tx.CommitAsync(ct);
        return recorded;
    }
    internal static async Task<bool> RecordInTransactionAsync(Guid id, RecordExecutionProgress update, DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        if (update.EventId == Guid.Empty || update.ExpectedRevision < 0 || update.Progress is < 0 or > 100 || update.Detail.Length > 4000 || !AllowedStates.Contains(update.State)) return false;
        await using (var rowLock = Sql.Command("SELECT id FROM executions WHERE id=@id FOR UPDATE", c, tx))
        { rowLock.Parameters.AddWithValue("id", id); if (await rowLock.ExecuteScalarAsync(ct) is null) return false; }
        await using (var duplicate = Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_events WHERE id=@event AND execution_id=@id)", c, tx))
        { duplicate.Parameters.AddWithValue("event", update.EventId); duplicate.Parameters.AddWithValue("id", id); if ((await duplicate.ScalarBoolAsync(ct))) return true; }
        await using var cmd = Sql.Command("""
   UPDATE executions SET state=@state,node_id=COALESCE(node_id,@node),progress=@progress,detail=@detail,
   revision=revision+1,updated_at=clock_timestamp(),
   started_at=CASE WHEN @state='running' THEN COALESCE(started_at,clock_timestamp()) ELSE started_at END,
   finished_at=CASE WHEN @state IN ('succeeded','failed','cancelled','deferred') THEN clock_timestamp() ELSE finished_at END
   WHERE id=@id AND revision=@revision AND (node_id IS NULL OR node_id=@node)
   AND ((state='queued' AND @state IN ('running','failed','cancelled'))
    OR (state='running' AND @state IN ('running','succeeded','failed','cancelled','deferred'))
    OR (state='cancelling' AND @state IN ('cancelled','failed')))
   AND (@state<>'running' OR @node IS NOT NULL)
   RETURNING revision
   """, c, tx);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("revision", update.ExpectedRevision);
        cmd.Parameters.AddWithValue("state", update.State); cmd.Parameters.AddWithValue("node", SqlKind.Uuid, (object?)update.NodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("progress", SqlKind.Decimal, (object?)update.Progress ?? DBNull.Value); cmd.Parameters.AddWithValue("detail", update.Detail);
        if (await cmd.ScalarInt32Async(ct) is not {} revision) return false;
        await AddEvent(c, tx, id, update.EventId, revision, update.State, update.Detail, ct); return true;
    }
    private static readonly HashSet<string> AllowedStates = ["running", "succeeded", "failed", "cancelled", "deferred"];
    private static async Task AddEvent(DbConnection c, DbTransaction tx, Guid id, Guid eventId, int revision, string state, string detail, CancellationToken ct)
    {
        await using var cmd = Sql.Command("INSERT INTO execution_events(id,execution_id,revision,state,detail) VALUES(@event,@id,@revision,@state,@detail)", c, tx);
        cmd.Parameters.AddWithValue("event", eventId); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("revision", revision); cmd.Parameters.AddWithValue("state", state); cmd.Parameters.AddWithValue("detail", detail); await cmd.ExecuteNonQueryAsync(ct);
    }
}
