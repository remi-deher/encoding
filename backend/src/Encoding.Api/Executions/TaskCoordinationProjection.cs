using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
namespace Encoding.Api.Executions;

/// <summary>Projects signed authority transitions; no network calls or media writes occur under writer admission.</summary>
public sealed class TaskCoordinationProjection(DbDataSource db,ClusterPeerIdentityStore identity,TaskCheckpointRepository checkpoints,TimeProvider? timeProvider=null)
{
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    internal static readonly TimeSpan RecoveryGrace=TimeSpan.FromSeconds(135);
    public async Task<int> ProjectAsync(CancellationToken ct)
    {
        await using(var hint=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_coordination_inbox)")) if(!await hint.ScalarBoolAsync(ct)) return 0;
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var rows=new List<(string Key,string Body)>();
        await using(var read=Sql.Command("SELECT register_key,body FROM task_coordination_inbox ORDER BY attempts,origin_id,sequence LIMIT 32",c,tx))
        await using(var r=await read.ExecuteReaderAsync(ct)) while(await r.ReadAsync(ct)) rows.Add((r.GetString(0),r.GetString(1)));
        if(rows.Count==0) return 0;
        foreach(var row in rows)
        {
            var(e,pending)=await checkpoints.VerifyAsync(row.Body,c,tx,ct);if(pending) {await DeferAsync(row.Key,row.Body,c,tx,ct);continue;}
            var applied=e is null || row.Key!=TaskCheckpointRepository.Prefix+e.EventId.ToString("N") ? false : await ApplyAsync(e,row.Body,c,tx,ct);
            if(applied is null) {await DeferAsync(row.Key,row.Body,c,tx,ct);continue;}
            if(!applied.Value)
            {
                await using var rejected=Sql.Command("INSERT INTO task_coordination_rejections(register_key,reason) VALUES(@key,'Événement de coordination invalide ou périmé.') ON CONFLICT DO NOTHING",c,tx);rejected.Parameters.AddWithValue("key",row.Key);await rejected.ExecuteNonQueryAsync(ct);
            }
            await using var remove=Sql.Command("DELETE FROM task_coordination_inbox WHERE register_key=@key AND body=@body",c,tx);remove.Parameters.AddWithValue("key",row.Key);remove.Parameters.AddWithValue("body",row.Body);await remove.ExecuteNonQueryAsync(ct);
        }
        await DeduplicateAsync(c,tx,ct);await tx.CommitAsync(ct);return rows.Count;
    }
    private static async Task DeferAsync(string key,string body,DbConnection c,DbTransaction tx,CancellationToken ct)
    {await using var cmd=Sql.Command("UPDATE task_coordination_inbox SET attempts=attempts+1 WHERE register_key=@key AND body=@body",c,tx);cmd.Parameters.AddWithValue("key",key);cmd.Parameters.AddWithValue("body",body);await cmd.ExecuteNonQueryAsync(ct);}
    private static async Task<bool> IsNeutralizedAsync(Guid peer,DbConnection c,DbTransaction tx,CancellationToken ct)
    {await using var cmd=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_neutralizations WHERE peer_id=@peer AND released_at IS NULL)",c,tx);cmd.Parameters.AddWithValue("peer",peer);return await cmd.ScalarBoolAsync(ct);}
    private async Task<bool?> ApplyAsync(ClusterTaskEvent e,string body,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        if(e.Kind is not ("readmit" or "neutralize" or "observation" or "checkpoint" or "abort" or "activate" or "recover")) return false;
        if(e.Kind=="readmit")
        {
            if(e.SubjectPeerId is not {} subject || e.RecordedAt is not {} at) return false;
            await using(var exists=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_neutralizations WHERE peer_id=@peer)",c,tx)) {exists.Parameters.AddWithValue("peer",subject);if(!await exists.ScalarBoolAsync(ct)) return null;}
            await using var release=Sql.Command("UPDATE task_neutralizations SET released_at=@at WHERE peer_id=@peer AND event_id=@event AND author_peer_id=@author AND released_at IS NULL",c,tx);
            release.Parameters.AddWithValue("at",at);release.Parameters.AddWithValue("peer",subject);release.Parameters.AddWithValue("event",(object?)e.NeutralizationId??DBNull.Value);release.Parameters.AddWithValue("author",e.AuthorPeerId);await release.ExecuteNonQueryAsync(ct);return true;
        }
        if(e.Kind=="neutralize")
        {
            if(e.SubjectPeerId is not {} subject || subject==e.AuthorPeerId || e.RecordedAt is not {} at || e.Reason is not {Length:>=8 and <=1000}) return false;
            if(at>clock.GetUtcNow()+TimeSpan.FromSeconds(15)) return null;
            await using var record=Sql.Command("INSERT INTO task_neutralizations(peer_id,event_id,author_peer_id,recorded_at,reason) VALUES(@peer,@event,@author,@at,@reason) ON CONFLICT(peer_id) DO UPDATE SET event_id=excluded.event_id,author_peer_id=excluded.author_peer_id,recorded_at=excluded.recorded_at,reason=excluded.reason,released_at=NULL WHERE task_neutralizations.released_at IS NOT NULL AND excluded.recorded_at>task_neutralizations.released_at",c,tx);
            record.Parameters.AddWithValue("peer",subject);record.Parameters.AddWithValue("event",e.EventId);record.Parameters.AddWithValue("author",e.AuthorPeerId);record.Parameters.AddWithValue("at",at);record.Parameters.AddWithValue("reason",e.Reason);await record.ExecuteNonQueryAsync(ct);
            if(subject==identity.PeerId && await IsNeutralizedAsync(subject,c,tx,ct)) {await using var stop=Sql.Command("UPDATE task_server_identity SET ready=0",c,tx);await stop.ExecuteNonQueryAsync(ct);} return true;
        }
        if(e.Kind=="observation")
        {
            if(e.SubjectPeerId is not {} subject || e.RecordedAt is not {} at || at>clock.GetUtcNow()+TimeSpan.FromSeconds(15) || e.Sequence<1) return false;
            if(at+TimeSpan.FromSeconds(45)<clock.GetUtcNow()) return true;
            await using var record=Sql.Command("INSERT INTO task_peer_observations(observer_peer_id,subject_peer_id,sequence,observed_at,expires_at,reachable) VALUES(@author,@peer,@seq,@at,@expiry,@up) ON CONFLICT(observer_peer_id,subject_peer_id) DO UPDATE SET sequence=excluded.sequence,observed_at=excluded.observed_at,expires_at=excluded.expires_at,reachable=excluded.reachable WHERE excluded.sequence>task_peer_observations.sequence",c,tx);
            record.Parameters.AddWithValue("author",e.AuthorPeerId);record.Parameters.AddWithValue("peer",subject);record.Parameters.AddWithValue("seq",e.Sequence);record.Parameters.AddWithValue("at",at);record.Parameters.AddWithValue("expiry",at+TimeSpan.FromSeconds(45));record.Parameters.AddWithValue("up",e.Reachable);await record.ExecuteNonQueryAsync(ct);return true;
        }
        Guid owner,last;long epoch;string state;
        await using(var read=Sql.Command("SELECT owner_peer_id,epoch,last_event_id,state FROM task_ownership WHERE execution_id=@id AND cluster_id=@cluster",c,tx))
        {read.Parameters.AddWithValue("id",e.ExecutionId);read.Parameters.AddWithValue("cluster",e.ClusterId);await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)) return null;owner=r.GetGuid(0);epoch=r.GetInt64(1);last=r.GetGuid(2);state=r.GetString(3);}
        if(last==e.EventId) return true;
        if(e.Kind=="checkpoint")
        {
            if(owner!=e.AuthorPeerId || epoch!=e.Epoch || last!=e.PreviousEventId || state=="quarantined") return e.Epoch<epoch?true:null;
            try {checkpoints.Decode(e);} catch(Exception ex) when(ex is System.Security.Cryptography.CryptographicException or JsonException or InvalidDataException or FormatException) {return false;}
            await TaskCheckpointRepository.StoreAsync(e,body,c,tx,ct);return true;
        }
        if(e.Kind=="abort")
        {
            if(owner!=e.AuthorPeerId || epoch!=e.Epoch || last!=e.PreviousEventId) return false;
            if(e.TransferId is not {} aborted || aborted==Guid.Empty) return false;
            await using(var remember=Sql.Command("INSERT INTO task_transfer_aborts(transfer_id,event_id) VALUES(@transfer,@event) ON CONFLICT DO NOTHING",c,tx)) {remember.Parameters.AddWithValue("transfer",aborted);remember.Parameters.AddWithValue("event",e.EventId);await remember.ExecuteNonQueryAsync(ct);}
            await using var abort=Sql.Command("UPDATE task_transfers SET state='aborted' WHERE id=@transfer AND state IN ('proposed','received'); UPDATE task_ownership SET state='active' WHERE execution_id=@id AND owner_peer_id=@author AND state='frozen'",c,tx);
            abort.Parameters.AddWithValue("transfer",(object?)e.TransferId??DBNull.Value);abort.Parameters.AddWithValue("id",e.ExecutionId);abort.Parameters.AddWithValue("author",e.AuthorPeerId);await abort.ExecuteNonQueryAsync(ct);return true;
        }
        if(e.Kind is not ("activate" or "recover") || e.TargetPeerId is not {} target || target==Guid.Empty || e.TransferId is null || e.TransferId==Guid.Empty || e.Epoch<2 || e.PreviousEventId==Guid.Empty) return false;
        if(e.Epoch!=epoch+1 || last!=e.PreviousEventId || state=="quarantined") return e.Epoch<=epoch?true:null;
        if(e.Kind=="activate" && e.AuthorPeerId!=owner) return false;
        if(e.Kind=="recover")
        {
            if(e.AuthorPeerId!=target || e.RecordedAt is not {} now) return false;
            await using var proof=Sql.Command("SELECT event_id,recorded_at FROM task_neutralizations WHERE peer_id=@peer AND released_at IS NULL",c,tx);proof.Parameters.AddWithValue("peer",owner);await using var r=await proof.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return null;if(e.NeutralizationId!=r.GetGuid(0) || now-r.GetFieldValue<DateTimeOffset>(1)<RecoveryGrace) return false;
        }
        if(target==identity.PeerId)
        {
            string snapshot;Guid source;
            await using(var received=Sql.Command("SELECT snapshot,target_source_node_id FROM task_transfers WHERE id=@transfer AND execution_id=@id AND epoch=@epoch AND state IN ('received','activated')",c,tx))
            {
                received.Parameters.AddWithValue("transfer",e.TransferId.Value);received.Parameters.AddWithValue("id",e.ExecutionId);received.Parameters.AddWithValue("epoch",epoch);await using var r=await received.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)||r.IsDBNull(1)) return null;snapshot=r.GetString(0);source=r.GetGuid(1);
            }
            var(proposal,pending)=await checkpoints.VerifyAsync(snapshot,c,tx,ct);if(proposal is null) return pending?null:false;
            var value=checkpoints.Decode(proposal);await checkpoints.ImportAsync(value,source,c,tx,ct);
        }
        if(owner==identity.PeerId && e.Kind=="activate" && state!="frozen") return false;
        await using(var update=Sql.Command("UPDATE task_ownership SET owner_peer_id=@target,epoch=@epoch,last_event_id=@event,state=@state WHERE execution_id=@id; UPDATE task_transfers SET state='activated' WHERE id=@transfer; UPDATE task_transfers SET state='aborted' WHERE execution_id=@id AND id<>@transfer AND epoch<@epoch AND state IN ('proposed','received'); UPDATE execution_work SET worker_node_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1 WHERE execution_id=@id; DELETE FROM disk_locks WHERE execution_id=@id; UPDATE media_reservations SET lease_until='infinity',lease_epoch=(SELECT lease_epoch FROM execution_work WHERE execution_id=@id) WHERE execution_id=@id",c,tx))
        {
            update.Parameters.AddWithValue("target",target);update.Parameters.AddWithValue("epoch",e.Epoch);update.Parameters.AddWithValue("event",e.EventId);update.Parameters.AddWithValue("state",owner==identity.PeerId && target!=identity.PeerId?"transferred":"active");update.Parameters.AddWithValue("id",e.ExecutionId);update.Parameters.AddWithValue("transfer",e.TransferId.Value);await update.ExecuteNonQueryAsync(ct);
        }
        if(target==identity.PeerId) await checkpoints.SaveAsync(e.ExecutionId,c,tx,ct);
        return true;
    }
    internal static async Task DeduplicateAsync(DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        // Terminal executions are not reservations for future explicit attempts of the same file.
        await using var dedup=Sql.Command("""
          UPDATE task_ownership SET state='active' WHERE state='quarantined' AND execution_id IN (SELECT execution_id FROM task_quarantines);
          DELETE FROM task_quarantines;
          INSERT INTO task_quarantines(execution_id,winner_id,reason)
          SELECT loser.execution_id,winner.execution_id,'Création concurrente du même média ; résultat conservé en quarantaine.'
          FROM task_ownership loser JOIN task_ownership winner ON winner.library_id=loser.library_id AND winner.media_key=loser.media_key AND winner.cluster_id=loser.cluster_id
          LEFT JOIN executions le ON le.id=loser.execution_id LEFT JOIN executions we ON we.id=winner.execution_id
          WHERE loser.state='active' AND winner.state='active' AND COALESCE(le.finished_at,'infinity')>winner.queued_at AND COALESCE(we.finished_at,'infinity')>loser.queued_at
          AND (winner.queued_at,winner.owner_peer_id,winner.execution_id)<(loser.queued_at,loser.owner_peer_id,loser.execution_id)
          ORDER BY winner.queued_at,winner.owner_peer_id,winner.execution_id ON CONFLICT(execution_id) DO NOTHING;
          UPDATE task_ownership SET state='quarantined' WHERE execution_id IN (SELECT execution_id FROM task_quarantines);
          UPDATE execution_work SET lease_until=NULL,worker_node_id=NULL,lease_epoch=lease_epoch+1 WHERE execution_id IN (SELECT q.execution_id FROM task_quarantines q JOIN task_ownership o ON o.execution_id=q.execution_id JOIN task_server_identity s ON s.peer_id=o.owner_peer_id) AND lease_until IS NOT NULL;
          """,c,tx);await dedup.ExecuteNonQueryAsync(ct);
    }
}
