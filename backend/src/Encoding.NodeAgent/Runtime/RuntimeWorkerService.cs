using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

public sealed class RuntimeWorkerService(NodeAgentOptions options,WorkerIdentityStore identities,RuntimeFlowRunner runner,ILogger<RuntimeWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var tasks=new List<Task>();
        try
        {
            await WorkerPolling.DelayAsync(0,ct);
            while(!ct.IsCancellationRequested)
            {
                tasks.RemoveAll(task=>task.IsCompleted);
                try
                {
                    var identity=await identities.GetAsync(ct);
                    if(identity.IsEnrolled && options.Roles.Contains(NodeRole.Worker))
                    {
                        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        using var client=new RuntimeClient(identity);
                        var capacity=await client.GetAsync<RuntimeCapacity>("capacity",timeout.Token);
                        if(tasks.Count>=capacity.MaxConcurrentTasks) {await WorkerPolling.DelayAsync(2000,ct);continue;}
                        var lease=await client.PostAsync<RuntimeLease>("claim",new { },timeout.Token);
                        if(lease is not null)
                        {
                            tasks.Add(Task.Run(async()=>
                            {
                                using var executionClient=new RuntimeClient(identity);
                                await runner.RunAsync(lease,executionClient,ct);
                            },CancellationToken.None));
                        }
                    }
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
                catch(Exception exception) { logger.LogWarning("Attribution worker momentanément indisponible : {Type}.",exception.GetType().Name); }
                await WorkerPolling.DelayAsync(2000,ct);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(tasks); }
    }
}
