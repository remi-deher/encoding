using System.Collections.Concurrent;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

public sealed class RuntimeTransferService(NodeAgentOptions options,WorkerIdentityStore identities,MediaScanner scanner,PublicationGuardRunner guards,ILogger<RuntimeTransferService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid,Task> active=new();
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                foreach(var pair in active) if(pair.Value.IsCompleted) active.TryRemove(pair.Key,out _);
                try
                {
                    var identity=await identities.GetAsync(ct);
                    if(identity.IsEnrolled)
                    {
                        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        using var client=new RuntimeClient(identity);
                        var transfers=await client.GetAsync<RuntimeTransfer[]>("transfers",timeout.Token);
                        foreach(var transfer in transfers)
                        {
                            if(active.Count>=Math.Max(1,options.MaxConcurrentTasks)||active.ContainsKey(transfer.ExecutionId)) continue;
                            var task=Task.Run(async()=> { using var transferClient=new RuntimeClient(identity); await TransferAsync(transfer,transferClient,ct); },CancellationToken.None);
                            active.TryAdd(transfer.ExecutionId,task);
                        }
                    }
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
                catch(Exception exception) { logger.LogWarning("Canal de transfert momentanément indisponible : {Type}.",exception.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(2),ct);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(active.Values); }
    }
    private static async Task<bool> TryRenewSourceAsync(RuntimeClient client,RuntimeTransfer transfer,CancellationToken ct)
    {
        try { await client.GetAsync<RuntimeSourceState>($"executions/{transfer.ExecutionId:D}/source?epoch={transfer.Epoch}",ct); return true; }
        catch(HttpRequestException) { return false; }
    }
    private async Task TransferAsync(RuntimeTransfer transfer,RuntimeClient client,CancellationToken ct)
    {
        using var watch=CancellationTokenSource.CreateLinkedTokenSource(ct); ct=watch.Token;
        var guard=Task.Run(async()=>
        {
            try
            {
                while(!watch.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15),watch.Token);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(watch.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await client.GetAsync<RuntimeSourceState>($"executions/{transfer.ExecutionId:D}/source?epoch={transfer.Epoch}",timeout.Token);
                    if((await client.ExecutionAsync(transfer.ExecutionId,timeout.Token)).State!="running") { watch.Cancel(); break; }
                }
            }
            catch(OperationCanceledException) when(watch.IsCancellationRequested) { }
            catch(Exception) { watch.Cancel(); }
        },CancellationToken.None);
        var directory=Path.Combine(options.DataDirectory,"transfers",transfer.ExecutionId.ToString("D")); Directory.CreateDirectory(directory);
        try
        {
            var original=SafeMediaPaths.Resolve(transfer.Root,transfer.MediaPath);
            if (transfer.CanWrite && (transfer.Publish || transfer.VerifyPublicationAccess)) await LibraryAccessVerifier.VerifyPublicationAsync(transfer.Root, ct);
            using var mediaLock=transfer.CanWrite?SafeMediaPaths.Lock(original,Path.Combine(options.DataDirectory,"media-locks")):null;
            if(!transfer.Publish)
            {
                if(transfer.Observation is {} observed) new MediaFileStamp(observed.Bytes,observed.LastWriteUtcTicks).Verify(original);
                async Task UploadAsync(string relative,bool main)
                {
                    var path=SafeMediaPaths.Resolve(transfer.Root,relative);
                    var temporary=Path.Combine(directory,"source-"+Guid.NewGuid().ToString("N")+Path.GetExtension(path));
                    try
                    {
                        await using(var source=File.OpenRead(path))
                        await using(var copy=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
                        { await source.CopyToAsync(copy,ct); await copy.FlushAsync(ct); copy.Flush(true); }
                        var sha=await SafeMediaPaths.DigestAsync(temporary,ct);
                        if(await SafeMediaPaths.DigestAsync(path,ct)!=sha) throw new IOException("La source a changé pendant sa copie.");
                        if(main && transfer.Observation is {} expected) new MediaFileStamp(expected.Bytes,expected.LastWriteUtcTicks).Verify(path);
                        await client.PutAsync(transfer.ExecutionId,transfer.Epoch,temporary,sha,main,ct,main?null:relative);
                    }
                    finally { if(File.Exists(temporary)) File.Delete(temporary); }
                }
                foreach(var input in transfer.InputPaths??[]) await UploadAsync(input,false);
                await UploadAsync(transfer.MediaPath,true);
            }
            else
            {
                if(transfer.ResultSha256 is null || transfer.ResultExtension is null || transfer.SourceSha256 is null) throw new IOException("Résultat de transfert incomplet.");
                if(transfer.ReplacementGraceHours is not null && await SourceReplacer.ResumeAsync(transfer.Root,transfer.ExecutionId,transfer.MediaPath,transfer.SourceSha256,transfer.ResultSha256,transfer.ResultExtension,ct) is {} resumed)
                {
                    var current=await client.ExecutionAsync(transfer.ExecutionId,ct);
                    await client.PostAsync<object>($"executions/{transfer.ExecutionId:D}/publication",new ConfirmRuntimePublication(transfer.Epoch,Guid.NewGuid(),current.Revision,$".encoding-results/{transfer.ExecutionId:D}/result{transfer.ResultExtension}",transfer.ResultSha256,resumed),ct);
                    return;
                }
                if(await SafeMediaPaths.DigestAsync(original,ct)!=transfer.SourceSha256) throw new IOException("La source a changé depuis le début du traitement.");
                var incoming=Path.Combine(directory,"result"+transfer.ResultExtension);
                await client.DownloadAsync(transfer.ExecutionId,transfer.Epoch,transfer.ResultSha256,incoming,ct);
                var before=await scanner.ScanAsync(original,ct);
                await scanner.ValidateAsync(incoming,transfer.MediaType,before,ct);
                var relative=$".encoding-results/{transfer.ExecutionId:D}/result{transfer.ResultExtension}";
                var target=SafeMediaPaths.Resolve(transfer.Root,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); target=SafeMediaPaths.Resolve(transfer.Root,relative);
                var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
                try
                {
                    if(!File.Exists(target))
                    {
                        await using(var source=File.OpenRead(incoming))
                        await using(var destination=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
                        { await source.CopyToAsync(destination,ct); await destination.FlushAsync(ct); destination.Flush(true); }
                        var state=await client.ExecutionAsync(transfer.ExecutionId,ct);
                        if(state.State!="running") return;
                        await client.GetAsync<RuntimeSourceState>($"executions/{transfer.ExecutionId:D}/source?epoch={transfer.Epoch}",ct);
                        if(await SafeMediaPaths.DigestAsync(original,ct)!=transfer.SourceSha256) throw new IOException("La source a changé avant publication.");
                        Encoding.Core.AtomicFilePublication.Publish(temporary,target);
                    }
                    if(await SafeMediaPaths.DigestAsync(target,ct)!=transfer.ResultSha256) throw new IOException("La sortie existante est différente du résultat validé.");
                    Encoding.Core.AtomicFilePublication.Confirm(target);
                    SourceReplacementReport? replacement=null;
                    if(transfer.ReplacementGraceHours is not null)
                    {
                        var verdict=await guards.CheckAsync(transfer,ct);
                        replacement=!verdict.Allowed?new SourceReplacementReport("failed",null,null,0,DateTimeOffset.UtcNow,verdict.Detail)
                        :await SourceReplacer.ReplaceAsync(transfer.Root,transfer.ExecutionId,transfer.MediaPath,incoming,transfer.SourceSha256,transfer.ResultSha256,transfer.ResultExtension,
                            async token=>(await client.ExecutionAsync(transfer.ExecutionId,token)).State=="running" && await TryRenewSourceAsync(client,transfer,token),ct);
                    }
                    var status=await client.ExecutionAsync(transfer.ExecutionId,ct);
                    await client.PostAsync<object>($"executions/{transfer.ExecutionId:D}/publication",new ConfirmRuntimePublication(transfer.Epoch,Guid.NewGuid(),status.Revision,relative,transfer.ResultSha256,replacement),ct);
                }
                finally { if(File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        catch(SafeMediaPaths.MediaLockBusyException) { }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(Exception exception)
        {
            logger.LogWarning("Transfert {ExecutionId} refusé : {Type}.",transfer.ExecutionId,exception.GetType().Name);
            try
            {
                var status=await client.ExecutionAsync(transfer.ExecutionId,ct);
                await client.PostAsync<object>($"executions/{transfer.ExecutionId:D}/source-failure",new FinishRuntimeExecution(transfer.Epoch,Guid.NewGuid(),status.Revision,"failed",exception is LibraryAccessException ? exception.Message : transfer.Publish ? "Échec de publication du résultat ; confirmation de publication non obtenue." : "Échec de préparation ou de transfert de la source ; traitement interrompu.",null,null),ct);
            }
            catch(Exception) { }
        }
        finally { watch.Cancel(); await guard; }
    }
}
