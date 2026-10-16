using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

public sealed class RuntimeRecoveryService(DbDataSource dataSource,ILogger<RuntimeRecoveryService> logger,
    Encoding.Api.Nodes.NodePresenceOptions? presence = null, Encoding.Api.Infrastructure.PeerCluster.IPeerAssignmentGate? assignment = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var lease = assignment?.AcquireLease(ct);
                if (assignment is not null && lease is null) { await Task.Delay(TimeSpan.FromSeconds(1), ct); continue; }
                await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
                await RuntimeRepository.FailUnavailableSourcesAsync(c, tx, presence?.OfflineAfter ?? TimeSpan.FromSeconds(30), ct);
                var expired=new List<(Guid Id,Guid? Worker,int Revision)>();
                await using(var cmd=Sql.Command("SELECT e.id,e.node_id,e.revision FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.state='cancelling' AND (w.lease_until IS NULL OR w.lease_until<=clock_timestamp()) LIMIT 100 FOR UPDATE OF e,w SKIP LOCKED",c,tx))
                await using(var r=await cmd.ExecuteReaderAsync(ct))
                { while(await r.ReadAsync(ct)) expired.Add((r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.GetInt32(2))); }
                foreach(var item in expired)
                {
                    await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,"cancelled",item.Worker,null,"Annulation terminée après expiration du bail ; la source est conservée."),c,tx,ct);
                    await RuntimeRepository.ReleaseAsync(item.Id,c,tx,ct);
                }
                await tx.CommitAsync(ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
            catch(Exception exception) { logger.LogWarning("Reprise du registre momentanément indisponible : {Type}.",exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(1),ct); } catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
        }
    }
}
