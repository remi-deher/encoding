using System.Text.Json;
using Encoding.Contracts.Flows;
using Npgsql;
using NpgsqlTypes;

namespace Encoding.Api.Flows;

public sealed class FlowRepository(NpgsqlDataSource dataSource)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT f.id,COALESCE(d.definition,v.definition)->>'name',COALESCE(d.definition,v.definition)->>'mediaType',f.current_version,
                   jsonb_array_length(COALESCE(d.definition,v.definition)->'nodes'),CASE WHEN d.flow_id IS NOT NULL THEN COALESCE(db.status,'pending') ELSE COALESCE(b.status,'pending') END,
                   GREATEST(f.updated_at,d.updated_at),d.flow_id IS NOT NULL
            FROM flows f LEFT JOIN flow_versions v ON v.flow_id=f.id AND v.version=f.current_version
            LEFT JOIN flow_drafts d ON d.flow_id=f.id LEFT JOIN flow_draft_backups db ON db.flow_id=d.flow_id AND db.revision=d.revision
            LEFT JOIN flow_backup_jobs b ON b.flow_id=v.flow_id AND b.version=v.version
            WHERE d.flow_id IS NOT NULL OR v.flow_id IS NOT NULL
            ORDER BY GREATEST(f.updated_at,d.updated_at) DESC,f.id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<FlowSummary>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetString(5),reader.GetFieldValue<DateTimeOffset>(6),reader.GetBoolean(7)));
        return result;
    }

    public async Task<FlowVersionResponse?> GetAsync(Guid id, int? version, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT v.definition::text,b.status,v.created_at FROM flows f
            JOIN flow_versions v ON v.flow_id=f.id AND v.version=COALESCE(@version,f.current_version)
            JOIN flow_backup_jobs b ON b.flow_id=v.flow_id AND b.version=v.version WHERE f.id=@id
            """);
        command.Parameters.AddWithValue("id",id);
        command.Parameters.AddWithValue("version",NpgsqlDbType.Integer,(object?)version ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),JsonOptions)!,reader.GetString(1),reader.GetFieldValue<DateTimeOffset>(2)) : null;
    }

    public async Task<FlowDraftResponse?> DraftAsync(Guid id,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("SELECT d.definition::text,d.revision,f.current_version,d.updated_at,b.status FROM flow_drafts d JOIN flows f ON f.id=d.flow_id JOIN flow_draft_backups b ON b.flow_id=d.flow_id AND b.revision=d.revision WHERE f.id=@id");
        command.Parameters.AddWithValue("id",id); await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),JsonOptions)!,reader.GetInt32(1),reader.GetInt32(2),reader.GetString(4),reader.GetFieldValue<DateTimeOffset>(3)) : null;
    }
    public async Task<FlowDraftResponse?> SaveDraftAsync(SaveFlowDraftRequest request,CancellationToken ct)
    {
        await using var connection=await dataSource.OpenConnectionAsync(ct); await using var tx=await connection.BeginTransactionAsync(ct);
        await using var command=new NpgsqlCommand(
          "INSERT INTO flows(id) VALUES(@id) ON CONFLICT DO NOTHING; SELECT current_version FROM flows WHERE id=@id FOR UPDATE",connection,tx);
        command.Parameters.AddWithValue("id",request.Definition.Id);
        var published=(int)(await command.ExecuteScalarAsync(ct))!;
        await using var save=new NpgsqlCommand(
          "INSERT INTO flow_drafts(flow_id,revision,definition) SELECT @id,1,@document WHERE @expected=0 ON CONFLICT(flow_id) DO UPDATE SET definition=EXCLUDED.definition,revision=flow_drafts.revision+1,updated_at=clock_timestamp() WHERE flow_drafts.revision=@expected RETURNING revision",connection,tx);
        save.Parameters.AddWithValue("id",request.Definition.Id); save.Parameters.AddWithValue("expected",request.ExpectedRevision);
        save.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(request.Definition,JsonOptions));
        // Update cannot use an INSERT filtered out by expected>0; lock and inspect first.
        await using var existing=new NpgsqlCommand("SELECT revision FROM flow_drafts WHERE flow_id=@id",connection,tx);
        existing.Parameters.AddWithValue("id",request.Definition.Id);
        var revision=await existing.ExecuteScalarAsync(ct);
        if((revision is null && request.ExpectedRevision!=0) || (revision is int current && current!=request.ExpectedRevision)) return null;
        await using var highest=new NpgsqlCommand("SELECT COALESCE(max(revision),0)+1 FROM flow_draft_backups WHERE flow_id=@id",connection,tx);
        highest.Parameters.AddWithValue("id",request.Definition.Id);
        var nextRevision=(int)(await highest.ExecuteScalarAsync(ct))!;
        save.Parameters.AddWithValue("next",nextRevision);
        save.CommandText="INSERT INTO flow_drafts(flow_id,revision,definition) VALUES(@id,@next,@document) ON CONFLICT(flow_id) DO UPDATE SET definition=EXCLUDED.definition,revision=EXCLUDED.revision,updated_at=clock_timestamp() RETURNING revision,updated_at";
        await using var written=await save.ExecuteReaderAsync(ct); await written.ReadAsync(ct); var result=new FlowDraftResponse(request.Definition,written.GetInt32(0),published,"draft",written.GetFieldValue<DateTimeOffset>(1)); await written.DisposeAsync();
        await using(var backup=new NpgsqlCommand("INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@id,@revision,@document)",connection,tx)) {
            backup.Parameters.AddWithValue("id",request.Definition.Id); backup.Parameters.AddWithValue("revision",result.DraftRevision); backup.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(request.Definition,JsonOptions)); await backup.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return result with { BackupStatus="pending" };
    }

    public async Task<IReadOnlyList<FlowDefinition>> VersionsAsync(Guid id, CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("SELECT definition::text FROM flow_versions WHERE flow_id=@id ORDER BY version");
        command.Parameters.AddWithValue("id",id);
        await using var reader=await command.ExecuteReaderAsync(ct);
        var list=new List<FlowDefinition>();
        while(await reader.ReadAsync(ct)) list.Add(JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),JsonOptions)!);
        return list;
    }

    /// <summary>CAS publication and backup outbox share one transaction. Versions are append-only.</summary>
    public async Task<bool> SaveAsync(SaveFlowRequest request, CancellationToken ct)
        => await SaveManyAsync([request], false, ct);

    public async Task<bool> SaveManyAsync(IReadOnlyList<SaveFlowRequest> requests, bool restore, CancellationToken ct,IReadOnlyList<FlowDraftImport>? drafts=null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach(var id in requests.Select(r=>r.Definition.Id).Concat((drafts??[]).Select(d=>d.Definition.Id)).Distinct().Order()) {
            await using var guard=new NpgsqlCommand("INSERT INTO flows(id) VALUES(@id) ON CONFLICT DO NOTHING; SELECT id FROM flows WHERE id=@id FOR UPDATE",connection,tx);
            guard.Parameters.AddWithValue("id",id); await guard.ExecuteScalarAsync(ct);
        }
        foreach (var request in requests.OrderBy(r => r.Definition.Id).ThenBy(r => r.Definition.Version))
        {
            if (!await SaveCoreAsync(request, restore, connection, tx, ct)) { await tx.RollbackAsync(ct); return false; }
        }
        foreach(var imported in drafts??[]) {
            var draft=imported.Definition;
            await using var existing=new NpgsqlCommand("SELECT definition=@document FROM flow_drafts WHERE flow_id=@id",connection,tx);
            existing.Parameters.AddWithValue("id",draft.Id); existing.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(draft,JsonOptions));
            if(await existing.ExecuteScalarAsync(ct) is bool same) { if(!same) return false; continue; }
            await using var historical=new NpgsqlCommand("SELECT definition=@document FROM flow_draft_backups WHERE flow_id=@id AND revision=@revision",connection,tx);
            historical.Parameters.AddWithValue("id",draft.Id); historical.Parameters.AddWithValue("revision",imported.Revision); historical.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(draft,JsonOptions));
            if(await historical.ExecuteScalarAsync(ct) is bool sameHistory && !sameHistory) return false;
            await using var save=new NpgsqlCommand("INSERT INTO flow_drafts(flow_id,revision,definition) VALUES(@id,@revision,@document); INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@id,@revision,@document) ON CONFLICT DO NOTHING",connection,tx);
            save.Parameters.AddWithValue("id",draft.Id); save.Parameters.AddWithValue("revision",imported.Revision); save.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(draft,JsonOptions));
            await save.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return true;
    }

    private static async Task<bool> SaveCoreAsync(SaveFlowRequest request, bool restore, NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        var definition = request.Definition;
        await using (var insert = new NpgsqlCommand("INSERT INTO flows(id) VALUES(@id) ON CONFLICT DO NOTHING",connection,tx))
        { insert.Parameters.AddWithValue("id",definition.Id); await insert.ExecuteNonQueryAsync(ct); }
        int current;
        await using (var read = new NpgsqlCommand("SELECT current_version FROM flows WHERE id=@id FOR UPDATE",connection,tx))
        { read.Parameters.AddWithValue("id",definition.Id); current = (int)(await read.ExecuteScalarAsync(ct))!; }
        var json = JsonSerializer.Serialize(definition,JsonOptions);
        // An identical retry is acknowledged even after a newer publication. It never changes the head.
        await using (var repeat = new NpgsqlCommand("SELECT definition = @document FROM flow_versions WHERE flow_id=@id AND version=@version",connection,tx))
        {
            repeat.Parameters.AddWithValue("id",definition.Id); repeat.Parameters.AddWithValue("version",definition.Version);
            repeat.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,json);
            var same = await repeat.ExecuteScalarAsync(ct);
            if (same is bool identical) return identical;
        }
        if(request.ExpectedDraftRevision is { } expectedDraft)
        {
            await using var draft=new NpgsqlCommand("SELECT revision FROM flow_drafts WHERE flow_id=@id",connection,tx);
            draft.Parameters.AddWithValue("id",definition.Id);
            if(await draft.ExecuteScalarAsync(ct) is not int revision || revision!=expectedDraft) return false;
        }
        if (!restore && (current != request.ExpectedVersion || definition.Version != current + 1)) return false;
        await using var command = new NpgsqlCommand("""
            INSERT INTO flow_versions(flow_id,version,definition) VALUES(@id,@version,@document);
            INSERT INTO flow_backup_jobs(flow_id,version) VALUES(@id,@version);
            UPDATE flows SET updated_at=CASE WHEN current_version<@version THEN clock_timestamp() ELSE updated_at END,current_version=GREATEST(current_version,@version) WHERE id=@id;
            """,connection,tx);
        command.Parameters.AddWithValue("id",definition.Id); command.Parameters.AddWithValue("version",definition.Version);
        command.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,json);
        await command.ExecuteNonQueryAsync(ct);
        if(request.ExpectedDraftRevision is { } draftRevision)
        {
            await using var clear=new NpgsqlCommand("DELETE FROM flow_drafts WHERE flow_id=@id AND revision=@revision",connection,tx);
            clear.Parameters.AddWithValue("id",definition.Id); clear.Parameters.AddWithValue("revision",draftRevision);
            await clear.ExecuteNonQueryAsync(ct);
        }
        return true;
    }
}
