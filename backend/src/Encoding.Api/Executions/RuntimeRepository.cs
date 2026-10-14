using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Encoding.Api.Executions;

public sealed class RuntimeRepository(NpgsqlDataSource dataSource,RuntimeEligibility eligibility,FlowComponentRegistry components,NodePresenceOptions presence,PluginConfigurationRepository pluginConfigurations)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public async Task<bool> AuthenticateAsync(Guid node,string token,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(token) || token.Length>256) return false;
        await using var cmd=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@node AND deleted_at IS NULL AND node_token_hash=@hash)");
        cmd.Parameters.AddWithValue("node",node); cmd.Parameters.AddWithValue("hash",SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> EnqueueAsync(EnqueueMediaRequest request,FlowDefinition flow,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await EnqueueInTransactionAsync(request,flow,c,tx,ct)) return false;
        await tx.CommitAsync(ct); return true;
    }

    internal static async Task<bool> EnqueueInTransactionAsync(EnqueueMediaRequest request,FlowDefinition flow,NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken ct)
    {
        var path=NormalizePath(request.MediaPath);
        await using var library=new NpgsqlCommand("SELECT name FROM libraries WHERE id=@id AND is_active FOR UPDATE",c,tx);
        library.Parameters.AddWithValue("id",request.LibraryId);
        if(await library.ExecuteScalarAsync(ct) is not string name) return false;
        await using var duplicate=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.library_id=@library AND w.media_key=@key AND e.state IN ('queued','running','cancelling'))",c,tx);
        duplicate.Parameters.AddWithValue("library",request.LibraryId); duplicate.Parameters.AddWithValue("key",path.ToUpperInvariant());
        if((bool)(await duplicate.ExecuteScalarAsync(ct))!) return false;
        var creation=new CreateExecutionRequest(request.Id,path,name,flow.Name,flow.Version,JsonSerializer.SerializeToElement(flow,Json));
        if(!await ExecutionRepository.CreateInTransactionAsync(creation,c,tx,ct)) return false;
        await using var work=new NpgsqlCommand("INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES(@id,@library,@path,@key)",c,tx);
        work.Parameters.AddWithValue("id",request.Id); work.Parameters.AddWithValue("library",request.LibraryId);
        work.Parameters.AddWithValue("path",path); work.Parameters.AddWithValue("key",path.ToUpperInvariant());
        await work.ExecuteNonQueryAsync(ct); return true;
    }

    public static string NormalizePath(string? path)=>Encoding.Contracts.Libraries.MediaPathRules.Normalize(path);

    public async Task<RuntimeLease?> ClaimAsync(Guid worker,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        IReadOnlyList<NodeToolCapability> capabilities; int capacity;
        await using(var node=new NpgsqlCommand("SELECT capabilities::text,COALESCE((SELECT p.max_concurrent_tasks FROM node_processing_settings p WHERE p.node_id=cluster_nodes.id),max_concurrent_tasks) FROM cluster_nodes WHERE id=@node AND deleted_at IS NULL AND 'worker'=ANY(roles) AND last_seen_at>clock_timestamp()-@offline FOR UPDATE",c,tx))
        {
            node.Parameters.AddWithValue("offline",presence.OfflineAfter); node.Parameters.AddWithValue("node",worker); await using var r=await node.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return null;
            capabilities=JsonSerializer.Deserialize<NodeToolCapability[]>(r.GetString(0),Json)??[]; capacity=r.GetInt32(1);
        }
        await using(var active=new NpgsqlCommand("SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.worker_node_id=@worker AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')",c,tx))
        { active.Parameters.AddWithValue("worker",worker); if((long)(await active.ExecuteScalarAsync(ct))!>=capacity) return null; }
        var candidates=new List<(Guid Id,Guid Library,string Path,string Key,FlowDefinition Flow,int Attempt,int Revision,long Epoch)>();
        await using(var read=new NpgsqlCommand("""
          SELECT e.id,w.library_id,w.media_path,w.media_key,e.flow_definition::text,e.attempt,e.revision,w.lease_epoch
          FROM execution_work w JOIN executions e ON e.id=w.execution_id JOIN libraries l ON l.id=w.library_id
          WHERE e.state IN ('queued','running') AND l.is_active AND w.available_at<=clock_timestamp()
          AND (w.lease_until IS NULL OR w.lease_until<=clock_timestamp())
          AND NOT EXISTS(SELECT 1 FROM library_processing_settings p WHERE p.library_id=l.id AND p.max_concurrent_tasks IS NOT NULL
            AND p.max_concurrent_tasks<=(SELECT count(*) FROM execution_work aw JOIN executions ae ON ae.id=aw.execution_id WHERE aw.library_id=l.id AND aw.lease_until>clock_timestamp() AND ae.state IN ('running','cancelling')))
          AND NOT EXISTS(SELECT 1 FROM library_node_processing_limits p WHERE p.library_id=l.id AND p.node_id=@worker
            AND p.max_concurrent_tasks<=(SELECT count(*) FROM execution_work aw JOIN executions ae ON ae.id=aw.execution_id WHERE aw.library_id=l.id AND aw.worker_node_id=@worker AND aw.lease_until>clock_timestamp() AND ae.state IN ('running','cancelling')))
          ORDER BY e.queued_at,e.id LIMIT 100
          FOR UPDATE OF e,w SKIP LOCKED
          """,c,tx))
        {
            read.Parameters.AddWithValue("worker",worker);
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),JsonSerializer.Deserialize<FlowDefinition>(r.GetString(4),Json)!,r.GetInt32(5),r.GetInt32(6),r.GetInt64(7)));
        }
        foreach(var item in candidates)
        {
            if(!eligibility.CanRun(item.Flow,capabilities,worker)) continue;
            // Library row serializes claims from all workers before counting active leases.
            await using(var limits=new NpgsqlCommand("""
              SELECT p.max_concurrent_tasks,(SELECT max_concurrent_tasks FROM library_node_processing_limits WHERE library_id=l.id AND node_id=@worker)
              FROM libraries l LEFT JOIN library_processing_settings p ON p.library_id=l.id
              WHERE l.id=@library AND l.is_active FOR UPDATE OF l SKIP LOCKED
              """,c,tx))
            {
                limits.Parameters.AddWithValue("library",item.Library); limits.Parameters.AddWithValue("worker",worker);
                await using var r=await limits.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) continue;
                int? global=r.IsDBNull(0)?null:r.GetInt32(0); int? local=r.IsDBNull(1)?null:r.GetInt32(1); await r.DisposeAsync();
                await using var count=new NpgsqlCommand("""
                  SELECT count(*)::integer,count(*) FILTER(WHERE w.worker_node_id=@worker)::integer
                  FROM execution_work w JOIN executions e ON e.id=w.execution_id
                  WHERE w.library_id=@library AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')
                  """,c,tx);
                count.Parameters.AddWithValue("library",item.Library); count.Parameters.AddWithValue("worker",worker);
                await using var counts=await count.ExecuteReaderAsync(ct); await counts.ReadAsync(ct);
                if(global is {} maximum && counts.GetInt32(0)>=maximum || local is {} maximumLocal && counts.GetInt32(1)>=maximumLocal) continue;
            }
            Guid source; string? root;
            var needsWrite=item.Flow.Nodes.Any(n=>components.Resolve(n.Component)!.RequiresWriteAccess);
            await using(var access=new NpgsqlCommand("""
              SELECT COALESCE(a.transfer_source_node_id,a.node_id),CASE WHEN a.mode='direct' THEN a.local_path END
              FROM library_node_accesses a JOIN cluster_nodes n ON n.id=a.node_id
              LEFT JOIN library_node_accesses s ON s.library_id=a.library_id AND s.node_id=a.transfer_source_node_id
              LEFT JOIN cluster_nodes sn ON sn.id=s.node_id
              WHERE a.library_id=@library AND a.node_id=@worker AND a.is_enabled AND a.can_read
              AND ((a.mode='direct' AND a.local_path IS NOT NULL AND (NOT @write OR a.can_write))
                OR (a.mode='transfer' AND s.mode='direct' AND s.is_enabled AND s.can_read AND (NOT @write OR s.can_write)
                    AND sn.deleted_at IS NULL AND sn.last_seen_at>clock_timestamp()-@offline))
              """,c,tx))
            {
                access.Parameters.AddWithValue("offline",presence.OfflineAfter); access.Parameters.AddWithValue("library",item.Library); access.Parameters.AddWithValue("worker",worker); access.Parameters.AddWithValue("write",needsWrite);
                await using var r=await access.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) continue;
                source=r.GetGuid(0); root=r.IsDBNull(1)?null:r.GetString(1);
            }
            var epoch=item.Epoch+1;
            await using(var reserve=new NpgsqlCommand("""
              INSERT INTO media_reservations(library_id,media_key,execution_id,lease_epoch,lease_until)
              VALUES(@library,@key,@id,@epoch,clock_timestamp()+interval '60 seconds')
              ON CONFLICT(library_id,media_key) DO UPDATE SET execution_id=@id,lease_epoch=@epoch,lease_until=clock_timestamp()+interval '60 seconds'
              WHERE media_reservations.lease_until<=clock_timestamp() RETURNING execution_id
              """,c,tx))
            {
                reserve.Parameters.AddWithValue("library",item.Library); reserve.Parameters.AddWithValue("key",item.Key);
                reserve.Parameters.AddWithValue("id",item.Id); reserve.Parameters.AddWithValue("epoch",epoch);
                if(await reserve.ExecuteScalarAsync(ct) is null) continue;
            }
            await using(var set=new NpgsqlCommand("""
              UPDATE execution_work SET worker_node_id=@worker,source_node_id=@source,lease_epoch=@epoch,
                 lease_until=clock_timestamp()+interval '60 seconds' WHERE execution_id=@id;
              UPDATE executions SET node_id=@worker WHERE id=@id;
              """,c,tx))
            { set.Parameters.AddWithValue("worker",worker); set.Parameters.AddWithValue("source",source); set.Parameters.AddWithValue("epoch",epoch); set.Parameters.AddWithValue("id",item.Id); await set.ExecuteNonQueryAsync(ct); }
            await using(var uncertain=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM execution_steps WHERE execution_id=@id AND completed_at IS NULL AND NOT idempotent)",c,tx))
            {
                uncertain.Parameters.AddWithValue("id",item.Id);
                if((bool)(await uncertain.ExecuteScalarAsync(ct))!)
                {
                    await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,"failed",worker,null,"Une étape externe a un effet incertain après interruption. Une relance explicite est nécessaire."),c,tx,ct);
                    await ReleaseAsync(item.Id,c,tx,ct); await tx.CommitAsync(ct); return null;
                }
            }
            await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,"running",worker,null,"Flow attribué au worker ; bail actif."),c,tx,ct);
            RuntimeCheckpoint? checkpoint=null; string? sourceSha=null; DateTimeOffset expires;
            await using(var state=new NpgsqlCommand("SELECT source_sha,lease_until,(SELECT checkpoint::text FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1) FROM execution_work WHERE execution_id=@id",c,tx))
            {
                state.Parameters.AddWithValue("id",item.Id); await using var r=await state.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
                sourceSha=r.IsDBNull(0)?null:r.GetString(0); expires=r.GetFieldValue<DateTimeOffset>(1);
                if(!r.IsDBNull(2)) checkpoint=JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(2),Json);
            }
            var pluginSettings = await pluginConfigurations.ResolveEffectiveAsync(item.Flow.Id,
                item.Flow.Nodes.Select(node => node.Component.PluginId).Where(id => id != "base"), c, tx, ct);
            await tx.CommitAsync(ct); return new(item.Id,item.Attempt,item.Revision+1,epoch,expires,source,root,item.Path,sourceSha,item.Flow,checkpoint,item.Flow.Nodes.Single(node=>components.Resolve(node.Component)?.Kind=="input").Id,pluginSettings);
        }
        return null;
    }

    internal static async Task<bool> FenceAsync(Guid id,Guid actor,long epoch,NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken ct,bool source=false)
    {
        await using var command=new NpgsqlCommand("""
          SELECT w.execution_id FROM execution_work w JOIN executions e ON e.id=w.execution_id
          JOIN media_reservations m ON m.execution_id=w.execution_id AND m.lease_epoch=w.lease_epoch
          WHERE w.execution_id=@id AND w.lease_epoch=@epoch AND w.lease_until>clock_timestamp() AND m.lease_until>clock_timestamp()
          AND (CASE WHEN @source THEN w.source_node_id ELSE w.worker_node_id END)=@actor AND e.state IN ('running','cancelling')
          FOR UPDATE OF e,w,m
          """,c,tx);
        command.Parameters.AddWithValue("id",id); command.Parameters.AddWithValue("actor",actor); command.Parameters.AddWithValue("epoch",epoch); command.Parameters.AddWithValue("source",source);
        return await command.ExecuteScalarAsync(ct) is not null;
    }
    internal static async Task ReleaseAsync(Guid id,NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("DELETE FROM media_reservations WHERE execution_id=@id; UPDATE execution_work SET lease_until=NULL WHERE execution_id=@id",c,tx);
        cmd.Parameters.AddWithValue("id",id); await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RuntimeLeaseStatus?> RenewAsync(Guid id,Guid worker,long epoch,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,epoch,c,tx,ct)) return null;
        await using var update=new NpgsqlCommand("""
          UPDATE execution_work SET lease_until=clock_timestamp()+interval '60 seconds' WHERE execution_id=@id;
          UPDATE media_reservations SET lease_until=clock_timestamp()+interval '60 seconds' WHERE execution_id=@id;
          SELECT e.state='cancelling',e.revision,w.lease_until FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id
          """,c,tx);
        update.Parameters.AddWithValue("id",id); await using var r=await update.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        var result=new RuntimeLeaseStatus(r.GetBoolean(0),r.GetInt32(1),r.GetFieldValue<DateTimeOffset>(2)); await r.DisposeAsync(); await tx.CommitAsync(ct); return result;
    }

    public async Task<StartedRuntimeStep?> StartStepAsync(Guid id,Guid worker,StartRuntimeStep request,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,request.Epoch,c,tx,ct)) return null;
        FlowDefinition flow;
        await using(var cmd=new NpgsqlCommand("SELECT flow_definition::text FROM executions WHERE id=@id AND state='running'",c,tx))
        { cmd.Parameters.AddWithValue("id",id); if(await cmd.ExecuteScalarAsync(ct) is not string json) return null; flow=JsonSerializer.Deserialize<FlowDefinition>(json,Json)!; }
        var node=flow.Nodes.SingleOrDefault(n=>n.Id==request.NodeId); if(node is null) return null;
        await using(var prior=new NpgsqlCommand("SELECT checkpoint::text FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1",c,tx))
        {
            prior.Parameters.AddWithValue("id",id); var json=await prior.ExecuteScalarAsync(ct) as string;
            var expected=json is null ? flow.Nodes.Single(n=>components.Resolve(n.Component)!.Kind=="input").Id : JsonSerializer.Deserialize<RuntimeCheckpoint>(json,Json)!.NextNodeId;
            if(expected!=node.Id) return null;
        }
        await using var start=new NpgsqlCommand("""
          INSERT INTO execution_steps(execution_id,node_id,invocation_id,idempotent) VALUES(@id,@node,@invocation,@idempotent) ON CONFLICT DO NOTHING;
          SELECT invocation_id,checkpoint::text FROM execution_steps WHERE execution_id=@id AND node_id=@node
          """,c,tx);
        start.Parameters.AddWithValue("id",id); start.Parameters.AddWithValue("node",request.NodeId); start.Parameters.AddWithValue("invocation",Guid.NewGuid());
        start.Parameters.AddWithValue("idempotent",node.Component.PluginId=="base" && node.Component.ComponentId!="base-script");
        await using var r=await start.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        var result=new StartedRuntimeStep(r.GetGuid(0),r.IsDBNull(1)?null:JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(1),Json));
        await r.DisposeAsync(); await tx.CommitAsync(ct); return result;
    }

    public async Task<bool> CompleteStepAsync(Guid id,Guid worker,CompleteRuntimeStep request,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,request.Epoch,c,tx,ct)) return false;
        FlowDefinition flow;
        await using(var cmd=new NpgsqlCommand("SELECT flow_definition::text FROM executions WHERE id=@id AND state='running'",c,tx))
        { cmd.Parameters.AddWithValue("id",id); if(await cmd.ExecuteScalarAsync(ct) is not string json) return false; flow=JsonSerializer.Deserialize<FlowDefinition>(json,Json)!; }
        var cp=request.Checkpoint;
        if(cp is null || cp.Context is null || cp.Context.Variables is null || cp.Context.Plan is null
            || !RuntimeArtifactStore.ValidDigest(cp.Context.CurrentSha256)
            || cp.Context.Extension is null || !System.Text.RegularExpressions.Regex.IsMatch(cp.Context.Extension,"^\\.[a-zA-Z0-9]{1,10}$")) return false;
        await using(var artifact=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM execution_artifacts WHERE execution_id=@id AND sha256=@sha)",c,tx))
        {
            artifact.Parameters.AddWithValue("id",id); artifact.Parameters.AddWithValue("sha",cp.Context.CurrentSha256);
            if(!(bool)(await artifact.ExecuteScalarAsync(ct))!) return false;
        }
        var node=flow.Nodes.SingleOrDefault(n=>n.Id==cp.NodeId); if(node is null) return false;
        var component=components.Resolve(node.Component)!;
        if(component.Kind=="terminal" ? cp.OutputId is not null || cp.TerminalResult is not("success" or "unchanged" or "failed" or "deferred")
            : cp.TerminalResult is not null || !component.Outputs.Any(o=>o.Id==cp.OutputId)) return false;
        if(cp.NextNodeId!=flow.Connections.SingleOrDefault(e=>e.SourceNodeId==node.Id && e.SourceOutputId==cp.OutputId)?.TargetNodeId) return false;
        var serialized=JsonSerializer.Serialize(cp,Json); if(serialized.Length>1048576) return false;
        await using(var existing=new NpgsqlCommand("SELECT checkpoint=@checkpoint FROM execution_steps WHERE execution_id=@id AND node_id=@node AND invocation_id=@invocation AND completed_at IS NOT NULL",c,tx))
        {
            existing.Parameters.AddWithValue("id",id); existing.Parameters.AddWithValue("node",cp.NodeId); existing.Parameters.AddWithValue("invocation",cp.InvocationId); existing.Parameters.AddWithValue("checkpoint",NpgsqlDbType.Jsonb,serialized);
            if(await existing.ExecuteScalarAsync(ct) is bool same) return same;
        }
        await using var update=new NpgsqlCommand("""
          UPDATE execution_steps SET completed_at=clock_timestamp(),completed_revision=@completed_revision,checkpoint=@checkpoint
          WHERE execution_id=@id AND node_id=@node AND invocation_id=@invocation AND completed_at IS NULL RETURNING node_id
          """,c,tx);
        update.Parameters.AddWithValue("completed_revision",request.ExpectedRevision+1); update.Parameters.AddWithValue("id",id); update.Parameters.AddWithValue("node",cp.NodeId); update.Parameters.AddWithValue("invocation",cp.InvocationId); update.Parameters.AddWithValue("checkpoint",NpgsqlDbType.Jsonb,serialized);
        update.Parameters.AddWithValue("before",NpgsqlDbType.Jsonb,(object?)cp.Context.BeforeMetadata?.GetRawText()??DBNull.Value);
        update.Parameters.AddWithValue("after",NpgsqlDbType.Jsonb,(object?)cp.Context.CurrentMetadata?.GetRawText()??DBNull.Value);
        if(await update.ExecuteScalarAsync(ct) is null) return false;
        await using(var metadata=new NpgsqlCommand("UPDATE execution_work SET before_metadata=@before,after_metadata=@after WHERE execution_id=@id",c,tx))
        {
            metadata.Parameters.AddWithValue("id",id);
            metadata.Parameters.AddWithValue("before",NpgsqlDbType.Jsonb,(object?)cp.Context.BeforeMetadata?.GetRawText()??DBNull.Value);
            metadata.Parameters.AddWithValue("after",NpgsqlDbType.Jsonb,(object?)cp.Context.CurrentMetadata?.GetRawText()??DBNull.Value);
            await metadata.ExecuteNonQueryAsync(ct);
        }
        if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,"running",worker,null,$"Étape terminée : {node.Name??component.Name}."),c,tx,ct)) return false;
        await tx.CommitAsync(ct); return true;
    }
    public async Task<RuntimeSourceState> SourceAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("SELECT source_sha FROM execution_work WHERE execution_id=@id");cmd.Parameters.AddWithValue("id",id);
        var source=await cmd.ExecuteScalarAsync(ct) as string;
        await using var inputs=dataSource.CreateCommand("SELECT input_path,sha256 FROM execution_inputs WHERE execution_id=@id");
        inputs.Parameters.AddWithValue("id",id); await using var r=await inputs.ExecuteReaderAsync(ct);
        var map=new Dictionary<string,string>(); while(await r.ReadAsync(ct)) map.Add(r.GetString(0),r.GetString(1));
        return new(source,map);
    }
}
