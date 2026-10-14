using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

/// <summary>Exécute, sur le nœud ayant l'accès direct, les restaurations demandées et la purge des sauvegardes après la période de grâce.</summary>
public sealed class SourceReplacementService(NodeAgentOptions options,WorkerIdentityStore identities,ILogger<SourceReplacementService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var identity=await identities.GetAsync(ct);
                if(identity.IsEnrolled)
                {
                    using var client=new RuntimeClient(identity);
                    foreach(var task in await client.GetAsync<RuntimeReplacementTask[]>("replacements",ct)) await HandleAsync(task,client,ct);
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
            catch(Exception exception) { logger.LogWarning("Canal de remplacement momentanément indisponible : {Type}.",exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(30),ct); } catch(OperationCanceledException) { break; }
        }
    }
    private async Task HandleAsync(RuntimeReplacementTask task,RuntimeClient client,CancellationToken ct)
    {
        ReportReplacementTask report;
        try
        {
            using var mediaLock=SafeMediaPaths.Lock(SafeMediaPaths.Resolve(task.Root,task.OriginalPath),Path.Combine(options.DataDirectory,"media-locks"));
            if(task.Action=="rollback")
            {
                var result=await SourceReplacer.RollbackAsync(task.Root,task,ct);
                report=result.Status=="restored"?new("rolled_back",null,result.ByteLength,result.ModifiedAt):new("failed",result.Detail);
            }
            else report=SourceReplacer.Finalize(task.Root,task,out var detail)?new("finalized",null):new("failed",detail);
        }
        catch(SafeMediaPaths.MediaLockBusyException) { return; }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception exception) { logger.LogWarning("Remplacement {ExecutionId} : {Type}.",task.ExecutionId,exception.GetType().Name); report=new("failed","Opération impossible ("+exception.GetType().Name+") ; la sauvegarde est conservée."); }
        await client.PostAsync<object>($"replacements/{task.ExecutionId:D}/report",report,ct);
    }
}
