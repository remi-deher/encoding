using Encoding.Contracts.Executions;
using Npgsql;

namespace Encoding.Api.Executions;

public sealed class RuntimeTransferRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<RuntimeTransfer>> ListAsync(Guid node,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("""
          SELECT w.execution_id,w.lease_epoch,a.local_path,w.media_path,w.source_sha,w.result_sha,w.result_extension,w.publication_requested,e.flow_definition::text,a.can_write,w.replacement_grace_hours
          FROM execution_work w JOIN executions e ON e.id=w.execution_id
          JOIN library_node_accesses a ON a.library_id=w.library_id AND a.node_id=w.source_node_id
          WHERE w.source_node_id=@node AND w.lease_until>clock_timestamp() AND e.state='running'
          AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.local_path IS NOT NULL
          AND (w.source_sha IS NULL OR (w.publication_requested AND w.published_path IS NULL AND a.can_write))
          ORDER BY e.queued_at LIMIT 20
          """);
        cmd.Parameters.AddWithValue("node",node); await using var r=await cmd.ExecuteReaderAsync(ct); var result=new List<RuntimeTransfer>();
        while(await r.ReadAsync(ct)) result.Add(new(r.GetGuid(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetBoolean(7),System.Text.Json.JsonSerializer.Deserialize<Encoding.Contracts.Flows.FlowDefinition>(r.GetString(8),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Nodes
            .Where(n=>n.Configuration.TryGetValue("sourceFile",out var f)&&f.ValueKind==System.Text.Json.JsonValueKind.String&&!string.IsNullOrWhiteSpace(f.GetString()))
            .Select(n=>RuntimeRepository.NormalizePath(n.Configuration["sourceFile"].GetString())).Distinct().ToArray(),System.Text.Json.JsonSerializer.Deserialize<Encoding.Contracts.Flows.FlowDefinition>(r.GetString(8),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.MediaType,r.GetBoolean(9),r.IsDBNull(10)?null:r.GetInt32(10)));
        return result;
    }
    public async Task<bool> ConfirmAsync(Guid id,Guid source,ConfirmRuntimePublication request,CancellationToken ct)
    {
        if(request.PublishedPath is null || request.PublishedPath.Length>2048 || !RuntimeArtifactStore.ValidDigest(request.Sha256)) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await RuntimeRepository.FenceAsync(id,source,request.Epoch,c,tx,ct,true)) return false;
        Guid worker; Guid library; string original; string sourceSha; string resultSha; int? graceHours;
        await using(var read=new NpgsqlCommand("SELECT worker_node_id,library_id,media_path,source_sha,result_sha,replacement_grace_hours FROM execution_work WHERE execution_id=@id AND result_sha=@sha AND publication_requested AND @path='.encoding-results/'||execution_id::text||'/result'||result_extension",c,tx))
        {
            read.Parameters.AddWithValue("id",id); read.Parameters.AddWithValue("sha",request.Sha256); read.Parameters.AddWithValue("path",request.PublishedPath);
            await using var row=await read.ExecuteReaderAsync(ct); if(!await row.ReadAsync(ct)) return false;
            worker=row.GetGuid(0); library=row.GetGuid(1); original=row.GetString(2); sourceSha=row.GetString(3); resultSha=row.GetString(4); graceHours=row.IsDBNull(5)?null:row.GetInt32(5);
        }
        var replacement=graceHours is null?null:request.Replacement;
        if(replacement is not null && !SourceReplacementRepository.ValidReport(id,original,replacement)) return false;
        var detail=replacement?.Status=="replaced"?"Résultat validé et publié ; source remplacée, sauvegarde conservée pendant la période de grâce."
            :graceHours is not null?"Résultat validé et publié ; remplacement non effectué, fichier source conservé"+(replacement?.Detail is {} reason?" : "+reason:"."):"Résultat validé et publié ; fichier source conservé.";
        if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,"succeeded",worker,100,detail),c,tx,ct)) return false;
        if(replacement is not null) await SourceReplacementRepository.RecordInTransactionAsync(id,library,source,original,sourceSha,resultSha,graceHours!.Value,replacement,c,tx,ct);
        await using var update=new NpgsqlCommand("UPDATE execution_work SET published_path=@path WHERE execution_id=@id",c,tx);
        update.Parameters.AddWithValue("id",id); update.Parameters.AddWithValue("path",request.PublishedPath); await update.ExecuteNonQueryAsync(ct);
        await RuntimeRepository.ReleaseAsync(id,c,tx,ct); await tx.CommitAsync(ct); return true;
    }
    public async Task<bool> FinishAsync(Guid id,Guid worker,FinishRuntimeExecution request,CancellationToken ct,bool sourceFailure=false)
    {
        if(request.State is not("succeeded" or "failed" or "cancelled" or "deferred") || request.Detail is null || request.Detail.Length>4000) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await RuntimeRepository.FenceAsync(id,worker,request.Epoch,c,tx,ct,sourceFailure)) return false;
        if(request.Commands is { Count: > 0 } commands)
        {
            if(commands.Count>64 || commands.Any(item=>string.IsNullOrWhiteSpace(item.NodeId) || item.NodeId.Length>128
                || item.InvocationId==Guid.Empty || !RuntimeRepository.ValidCommandLog(item.Command))
                || commands.Select(item=>item.Command.Id).Distinct().Count()!=commands.Count) return false;
            var json=new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            if(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(commands,json).Length>524288) return false;
            foreach(var group in commands.GroupBy(item=>(item.NodeId,item.InvocationId)))
            {
                var logs=System.Text.Json.JsonSerializer.Serialize(group.Select(item=>item.Command),json);
                await using var append=new NpgsqlCommand("UPDATE execution_steps SET command_logs=command_logs || @logs WHERE execution_id=@id AND node_id=@node AND invocation_id=@invocation",c,tx);
                append.Parameters.AddWithValue("id",id); append.Parameters.AddWithValue("node",group.Key.NodeId);
                append.Parameters.AddWithValue("invocation",group.Key.InvocationId);
                append.Parameters.AddWithValue("logs",NpgsqlTypes.NpgsqlDbType.Jsonb,logs);
                if(await append.ExecuteNonQueryAsync(ct)!=1) return false;
            }
        }
        if(request.State=="cancelled")
        {
            await using var cancellation=new NpgsqlCommand("SELECT state='cancelling' FROM executions WHERE id=@id",c,tx); cancellation.Parameters.AddWithValue("id",id);
            if(!(bool)(await cancellation.ExecuteScalarAsync(ct))!) return false;
        }
        if(sourceFailure)
        {
            if(request.State!="failed") return false;
            await using var read=new NpgsqlCommand("SELECT worker_node_id FROM execution_work WHERE execution_id=@id",c,tx); read.Parameters.AddWithValue("id",id); worker=(Guid)(await read.ExecuteScalarAsync(ct))!;
        }
        if(request.State is "succeeded" or "deferred")
        {
            await using var reached=new NpgsqlCommand("SELECT checkpoint::text FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1",c,tx);
            reached.Parameters.AddWithValue("id",id);
            if(await reached.ExecuteScalarAsync(ct) is not string json) return false;
            var cp=System.Text.Json.JsonSerializer.Deserialize<RuntimeCheckpoint>(json,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            if(cp.NextNodeId is not null || cp.OutputId=="error" || cp.TerminalResult=="failed") return false;
            if(request.State=="deferred" && (cp.TerminalResult!="deferred" || request.DeferMinutes is not >=1 or >1440)) return false;
            if(request.BeforeMetadata is null || request.AfterMetadata is null) return false;
        }
        await using(var metadata=new NpgsqlCommand("UPDATE execution_work SET before_metadata=COALESCE(@before,before_metadata),after_metadata=COALESCE(@after,after_metadata) WHERE execution_id=@id",c,tx))
        {
            metadata.Parameters.AddWithValue("id",id);
            metadata.Parameters.AddWithValue("before",NpgsqlTypes.NpgsqlDbType.Jsonb,(object?)request.BeforeMetadata?.GetRawText()??DBNull.Value);
            metadata.Parameters.AddWithValue("after",NpgsqlTypes.NpgsqlDbType.Jsonb,(object?)request.AfterMetadata?.GetRawText()??DBNull.Value);
            await metadata.ExecuteNonQueryAsync(ct);
        }
        if(request.State=="succeeded" && request.ResultSha256 is { } sha)
        {
            if(!RuntimeArtifactStore.ValidDigest(sha) || request.Extension is null || !System.Text.RegularExpressions.Regex.IsMatch(request.Extension,"^\\.[a-zA-Z0-9]{1,10}$")) return false;
            await using var set=new NpgsqlCommand("""
              UPDATE execution_work w SET result_sha=@sha,result_extension=@extension,publication_requested=true,
              replacement_grace_hours=(SELECT p.grace_period_hours FROM library_replacement_policies p WHERE p.library_id=w.library_id AND p.enabled)
              WHERE execution_id=@id AND EXISTS(SELECT 1 FROM execution_artifacts a WHERE a.execution_id=w.execution_id AND a.sha256=@sha)
              AND EXISTS(SELECT 1 FROM library_node_accesses a WHERE a.library_id=w.library_id AND a.node_id=w.source_node_id AND a.is_enabled AND a.can_write)
              """,c,tx);
            set.Parameters.AddWithValue("id",id); set.Parameters.AddWithValue("sha",sha); set.Parameters.AddWithValue("extension",request.Extension);
            if(await set.ExecuteNonQueryAsync(ct)!=1) return false;
            if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,"running",worker,null,"Résultat validé ; publication demandée au nœud d’accès."),c,tx,ct)) return false;
        }
        else
        {
            if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,request.State,worker,request.State=="succeeded"?100:null,request.Detail),c,tx,ct)) return false;
            await RuntimeRepository.ReleaseAsync(id,c,tx,ct);
            if(request.State=="deferred")
            {
                var next=Guid.NewGuid();
                await using var schedule=new NpgsqlCommand("""
                  INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,retry_of,detail)
                  SELECT @next,media,library_name,flow_name,flow_version,flow_definition,'queued',attempt+1,id,'Nouvelle tentative planifiée après report.' FROM executions WHERE id=@id;
                  INSERT INTO execution_work(execution_id,library_id,media_path,media_key,available_at)
                  SELECT @next,library_id,media_path,media_key,clock_timestamp()+@delay FROM execution_work WHERE execution_id=@id;
                  INSERT INTO execution_events(id,execution_id,revision,state,detail) VALUES(@event,@next,0,'queued','Nouvelle tentative planifiée après report.');
                  """,c,tx);
                schedule.Parameters.AddWithValue("id",id); schedule.Parameters.AddWithValue("next",next); schedule.Parameters.AddWithValue("event",Guid.NewGuid());
                schedule.Parameters.AddWithValue("delay",TimeSpan.FromMinutes(request.DeferMinutes!.Value)); await schedule.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct); return true;
    }
}
