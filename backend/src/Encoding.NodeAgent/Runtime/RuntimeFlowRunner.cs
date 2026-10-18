using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.NodeAgent.Runtime;

public sealed class RuntimeFlowRunner(NodeAgentOptions options,MediaScanner scanner,MediaActions actions,ProcessToolRunner tools,PluginProcessHost plugins,ILogger<RuntimeFlowRunner> logger)
{
    public async Task RunAsync(RuntimeLease lease,RuntimeClient client,CancellationToken stoppingToken)
    {
        using var commandCapture=RuntimeCommandCapture.Begin();
        using var job=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var leaseGate=new SemaphoreSlim(1,1);
        var revision=lease.Revision; var cancelled=false; var lost=false;
        var directory=Path.Combine(options.DataDirectory,"executions",lease.ExecutionId.ToString("D"),lease.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(directory);
        async Task<RuntimeLeaseStatus> RenewAsync(CancellationToken ct)
        {
            await leaseGate.WaitAsync(ct);
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var status=(await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew",new RuntimeFence(lease.Epoch),timeout.Token))!;
                Volatile.Write(ref revision,status.Revision);
                if(status.CancelRequested) { cancelled=true; job.Cancel(); }
                return status;
            }
            finally { leaseGate.Release(); }
        }
        if(lease.Probe is { } wanted) { await ProbeAsync(lease,wanted,client,directory,stoppingToken); return; }
        var renewal=Task.Run(async()=>
        {
            try { while(!job.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(15),job.Token); await RenewAsync(job.Token); } }
            catch(OperationCanceledException) when(job.IsCancellationRequested) { }
            catch(Exception) { lost=true; job.Cancel(); }
        },CancellationToken.None);
        try
        {
            string? sourceSha=lease.SourceSha256;
            while(sourceSha is null)
            {
                var source=await client.GetAsync<RuntimeSourceState>($"executions/{lease.ExecutionId:D}/source?epoch={lease.Epoch}",job.Token);
                sourceSha=source.SourceSha256; if(sourceSha is null) await Task.Delay(TimeSpan.FromSeconds(2),job.Token);
            }
            var extension=Path.GetExtension(lease.MediaPath); if(string.IsNullOrWhiteSpace(extension)) extension=".mkv";
            var original=Path.Combine(directory,"source"+extension);
            RuntimeContext context=lease.Checkpoint?.Context??new(sourceSha,extension,new Dictionary<string,JsonElement>(),null,null,[]);
            var variables=new Dictionary<string,JsonElement>(context.Variables);
            var next=lease.Checkpoint?.NextNodeId;
            if(lease.Checkpoint is null) next=lease.EntryNodeId;
            var terminal=lease.Checkpoint?.TerminalResult; var lastOutput=lease.Checkpoint?.OutputId;
            var current=Path.Combine(directory,"current"+context.Extension);
            var materialized=false;

            async Task EnsureFilesAsync()
            {
                if(materialized) return;
                await client.DownloadAsync(lease.ExecutionId,lease.Epoch,sourceSha,original,job.Token);
                await client.DownloadAsync(lease.ExecutionId,lease.Epoch,context.CurrentSha256,current,job.Token);
                foreach(var variable in variables.Where(pair=>pair.Key.StartsWith("artifact.",StringComparison.Ordinal)))
                {
                    var sha=variable.Value.GetProperty("sha256").GetString()!;
                    var name=variable.Value.GetProperty("name").GetString()!;
                    var artifactDirectory=Path.Combine(directory,"artifacts",sha); Directory.CreateDirectory(artifactDirectory);
                    await client.DownloadAsync(lease.ExecutionId,lease.Epoch,sha,SafeMediaPaths.Resolve(artifactDirectory,Path.GetFileName(name)),job.Token);
                }
                materialized=true;
            }

            async Task EnsureScanAsync()
            {
                await EnsureFilesAsync();
                if(context.BeforeMetadata is null)
                {
                    if(await SafeMediaPaths.DigestAsync(original,job.Token)!=sourceSha) throw new IOException("La copie de référence a été altérée.");
                    context=context with { BeforeMetadata=await scanner.ScanAsync(original,job.Token) };
                }
                if(context.CurrentMetadata is null) context=context with { CurrentMetadata=await scanner.ScanAsync(current,job.Token) };
            }
            async Task<string> ExtraAsync(string path)
            {
                path=path.Replace('\\','/');
                RuntimeSourceState state;
                while(true)
                {
                    state=await client.GetAsync<RuntimeSourceState>($"executions/{lease.ExecutionId:D}/source?epoch={lease.Epoch}",job.Token);
                    if(state.Inputs?.TryGetValue(path,out _)==true) break;
                    await Task.Delay(TimeSpan.FromSeconds(2),job.Token);
                }
                var file=Path.Combine(directory,"input-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16]+Path.GetExtension(path));
                await client.DownloadAsync(lease.ExecutionId,lease.Epoch,state.Inputs![path],file,job.Token); return file;
            }
            async Task ApplyOutcomeAsync(MediaActionOutcome outcome,string node)
            {
                if(outcome.VideoEncodings is { Count: > 0 })
                    context=context with { VideoEncodings=[..(context.VideoEncodings??[]),..outcome.VideoEncodings] };
                foreach(var additional in outcome.Additional)
                {
                    var sha=await SafeMediaPaths.DigestAsync(additional,job.Token);
                    await client.PutAsync(lease.ExecutionId,lease.Epoch,additional,sha,false,job.Token);
                    variables["artifact."+node+"."+Path.GetFileName(additional)]=JsonSerializer.SerializeToElement(new { sha256=sha,name=Path.GetFileName(additional) });
                }
                if(outcome.Primary is { } candidate)
                {
                    var metadata=await scanner.ValidateAsync(candidate,lease.Flow.MediaType,context.BeforeMetadata,job.Token);
                    var sha=await SafeMediaPaths.DigestAsync(candidate,job.Token);
                    await client.PutAsync(lease.ExecutionId,lease.Epoch,candidate,sha,false,job.Token);
                    current=candidate; context=context with { CurrentSha256=sha,Extension=Path.GetExtension(candidate),CurrentMetadata=metadata };
                }
            }
            while(next is not null)
            {
                job.Token.ThrowIfCancellationRequested(); var node=lease.Flow.Nodes.Single(n=>n.Id==next);
                var started=(await client.PostAsync<StartedRuntimeStep>($"executions/{lease.ExecutionId:D}/steps/start",new StartRuntimeStep(lease.Epoch,node.Id),job.Token))!;
                if(started.Completed is { } completed)
                {
                    context=completed.Context; variables=new(context.Variables); next=completed.NextNodeId; terminal=completed.TerminalResult; lastOutput=completed.OutputId;
                    current=Path.Combine(directory,"current"+context.Extension); materialized=false; continue;
                }
                using var commandStep=commandCapture.BeginStep(node.Id,started.InvocationId);
                string? output="success"; terminal=null;
                RuntimeHandoffSelector? handoff=null; RuntimeWait? wait=null;
                try
                {
                    var id=node.Component.ComponentId; var c=node.Configuration;
                    if(id.StartsWith("base-input-",StringComparison.Ordinal)) { if(id!="base-input-file") await EnsureScanAsync(); }
                    else if(id=="base-variable")
                    {
                        variables[MediaActions.Text(c,"name")]=FlowVariableValue.Parse(MediaActions.Text(c,"value"));
                    }
                    else if(id=="base-prepare-processing")
                    {
                        await EnsureScanAsync(); context=context with { Plan=[] };
                        variables["plan.tool"]=JsonSerializer.SerializeToElement(MediaActions.Text(c,"tool","ffmpeg"));
                        variables["plan.preserveUnchangedTracks"]=JsonSerializer.SerializeToElement(MediaActions.Flag(c,"preserveUnchangedTracks",true));
                    }
                    else if(id=="base-complete-flow") { await EnsureScanAsync(); output=null; terminal=MediaActions.Text(c,"result","unchanged"); }
                    else if(id=="base-defer-processing") { await EnsureScanAsync(); output=null; terminal="deferred"; variables["runtime.deferMinutes"]=JsonSerializer.SerializeToElement(int.Parse(MediaActions.Number(c,"delayMinutes",2))); }
                    else if(id is "base-disk-lock" or "base-disk-unlock")
                    {
                        var disk=DiskLockKey.Normalize(MediaActions.Text(c,"disk"));
                        var request=new DiskLockRequest(lease.Epoch,disk);
                        if(id=="base-disk-lock")
                        {
                            var taken=(await client.PostAsync<DiskLockResponse>($"executions/{lease.ExecutionId:D}/disk-locks/acquire",request,job.Token))!;
                            output=taken.Acquired?"acquired":"busy";
                            variables["disk."+disk]=JsonSerializer.SerializeToElement(new { acquired=taken.Acquired,holder=taken.HolderExecutionId });
                        }
                        else await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/disk-locks/release",request,job.Token);
                    }
                    else if(id is "base-script" or "base-condition")
                    {
                        await EnsureFilesAsync();
                        if(id=="base-condition") await EnsureScanAsync();
                        var result=await ScriptAsync(id,c,new { variables,metadata=context.CurrentMetadata,sourceMetadata=context.BeforeMetadata,currentFile=Path.GetFileName(current) },directory,job.Token);
                        output=result.TryGetProperty("outputId",out var selected)?selected.GetString():"success";
                        if(result.TryGetProperty("variables",out var changes)) foreach(var change in changes.EnumerateObject()) variables[change.Name]=change.Value.Clone();
                        if(result.TryGetProperty("candidate",out var candidate)) { await EnsureScanAsync(); await ApplyOutcomeAsync(new(SafeMediaPaths.Resolve(directory,candidate.GetString()!),[]),node.Id); }
                    }
                    else if(node.Component.PluginId=="conditions" && node.Component.ComponentId=="conditions-match")
                    {
                        var tree=ConditionTree.Parse(c["rules"],lease.Flow.MediaType);
                        if(ConditionTree.RequiresMetadata(tree) && (context.CurrentMetadata is null || context.BeforeMetadata is null)) await EnsureScanAsync();
                        output=ConditionTree.Evaluate(tree,new(context with { Variables=variables },lease.SourceNodeId,client.NodeId,
                            lease.LibraryId,lease.MediaPath,sourceSha))?"yes":"no";
                    }
                    else if(node.Component.PluginId=="conditions" && MediaConditionEvaluator.IsMediaCondition(node.Component.ComponentId))
                    {
                        if(context.CurrentMetadata is null) await EnsureScanAsync();
                        output=MediaConditionEvaluator.Evaluate(node.Component.ComponentId,c,context.CurrentMetadata);
                    }
                    else if(RuntimeWaitPolicy.IsWait(node)) { wait=RuntimeWaitPolicy.Resolve(node); output="resumed"; }
                    else if(RuntimeHandoffPolicy.IsHandoff(node))
                    {
                        handoff=RuntimeHandoffPolicy.Resolve(node,lease.PluginConfigurations?.GetValueOrDefault("nodes"));
                        if(handoff.Mode=="node" && handoff.TargetNodeId==client.NodeId)
                            throw new IOException("Le relais doit sélectionner un autre worker.");
                    }
                    else if(node.Component.PluginId=="nodes" && node.Component.ComponentId==RuntimeRetryPolicy.ComponentId)
                    {
                        // This block declares an execution policy; retry decisions are made by the server after a lost lease.
                    }
                    else if(node.Component.PluginId=="nodes"
                        && (node.Component.ComponentId is "nodes-run-on-node" or "nodes-require-capability" or "nodes-require-direct-access"))
                    {
                        // Scheduling enforces these constraints before assigning the file to this worker.
                    }
                    else if(node.Component.PluginId is not ("base" or "video" or "audio" or "subtitles" or "mkv" or "images" or "probe" or "ai"))
                    {
                        await EnsureScanAsync();
                        var invocation=new FlowStepInvocation(1,started.InvocationId,lease.ExecutionId,lease.Attempt,client.NodeId,lease.Epoch,node.Id,node.Component,c,
                            new(sourceSha,Path.GetFileName(current),context.BeforeMetadata,context.CurrentMetadata,variables,lease.MediaPath,lease.LibraryId),DateTimeOffset.UtcNow.AddHours(6),
                            lease.PluginConfigurations?.GetValueOrDefault(node.Component.PluginId));
                        var response=await plugins.ExecuteAsync(invocation,directory,job.Token);
                        output=response.OutputId; terminal=response.TerminalResult?.ToString().ToLowerInvariant();
                        foreach(var change in response.VariableChanges) variables[change.Key]=change.Value;
                        if(response.CandidateArtifactId is { } candidate) await ApplyOutcomeAsync(new(SafeMediaPaths.Resolve(directory,candidate),[]),node.Id);
                    }
                    else if(id is "base-track-names" or "subtitles-track-names")
                    {
                        await EnsureScanAsync();
                        var outcome=await actions.TrackNamesAsync(c,context.CurrentMetadata!.Value,variables.TryGetValue("tracks.origin",out var saved)?saved:null,current,directory,node.Id,job.Token);
                        if(MediaActions.Text(c,"mode","apply")=="snapshot") { if(outcome.Value is { } snapshot) variables["tracks.origin"]=snapshot; }
                        else if(outcome.Primary is null) output="unchanged";
                        else
                        {
                            if(outcome.Value is { ValueKind: JsonValueKind.Array } notes) foreach(var note in notes.EnumerateArray()) logger.LogInformation("Noms des pistes : {Note}",note.GetString());
                            await ApplyOutcomeAsync(outcome with { Value=null },node.Id);
                        }
                    }
                    else if(id is "base-subtitle-manage" or "subtitles-manage")
                    {
                        await EnsureScanAsync();
                        var outcome=await actions.SubtitleManageAsync(c,context.CurrentMetadata!.Value,current,directory,node.Id,job.Token);
                        if(outcome.Primary is null) output="unchanged";
                        else
                        {
                            if(outcome.Value is { ValueKind: JsonValueKind.Array } notes) foreach(var note in notes.EnumerateArray()) logger.LogInformation("Sous-titres : {Note}",note.GetString());
                            await ApplyOutcomeAsync(outcome with { Value=null },node.Id);
                        }
                    }
                    else if(id is "base-add-fonts" or "subtitles-add-fonts")
                    {
                        await EnsureScanAsync();
                        var outcome=await actions.AddFontsAsync(c,current,directory,node.Id,job.Token);
                        if(outcome.Primary is null) output="unchanged";
                        else
                        {
                            if(outcome.Value is { ValueKind: JsonValueKind.Array } added) foreach(var font in added.EnumerateArray()) logger.LogInformation("Police ajoutée : {Font}",font.GetString());
                            await ApplyOutcomeAsync(outcome with { Value=null },node.Id);
                        }
                    }
                    else if(id is "base-audio-rules" or "audio-rules")
                    {
                        await EnsureScanAsync();
                        var rules=AudioRuleOptions.From(c); var audioPlan=AudioRulePlanner.Plan(context.CurrentMetadata!.Value,rules);
                        foreach(var decision in audioPlan.Decisions) logger.LogInformation("Audio piste {Track} : {Note}",decision.AudioIndex,decision.Note);
                        var steps=audioPlan.Steps(node.Id,rules);
                        if(steps.Count==0) output="unchanged";
                        else if(MediaActions.Text(c,"processingMode","plan")=="plan")
                        {
                            if(variables.TryGetValue("plan.tool",out var selected) && selected.GetString()!="ffmpeg")
                                throw new MediaToolException("outil du plan incompatible avec cette action",1);
                            context=context with { Plan=[..context.Plan,..steps] };
                        }
                        else await ApplyOutcomeAsync(await actions.FfmpegPlanAsync(steps,current,directory,node.Id,client.NodeId,job.Token),node.Id);
                    }
                    else if(id is "base-auto-crop" or "video-auto-crop")
                    {
                        await EnsureScanAsync();
                        var duration=0.0;
                        if(context.CurrentMetadata is { } meta && meta.TryGetProperty("format",out var fmt) && fmt.TryGetProperty("duration",out var durProp) && durProp.ValueKind==JsonValueKind.String && double.TryParse(durProp.GetString(),System.Globalization.CultureInfo.InvariantCulture,out var d))
                            duration=d;

                        var (w,h)=(0,0);
                        if(context.CurrentMetadata is { } m && m.TryGetProperty("streams",out var str))
                        {
                            foreach(var s in str.EnumerateArray())
                            {
                                if(s.TryGetProperty("codec_type",out var ctProp) && ctProp.GetString()=="video")
                                {
                                    if(s.TryGetProperty("width",out var wP)) w=wP.GetInt32();
                                    if(s.TryGetProperty("height",out var hP)) h=hP.GetInt32();
                                    break;
                                }
                            }
                        }

                        var samplePoints=int.Parse(MediaActions.Number(c,"samplePoints",5));
                        var sampleSecs=int.Parse(MediaActions.Number(c,"sampleDurationSeconds",2));
                        var threshold=int.Parse(MediaActions.Number(c,"threshold",24));
                        var roundMul=int.Parse(MediaActions.Number(c,"roundMultiple",2));

                        var crop=await actions.DetectCropAsync(current,directory,samplePoints,sampleSecs,threshold,roundMul,duration,job.Token);
                        if(crop is not null && crop.IsSignificant(w,h))
                        {
                            output="cropped";
                            variables["video.crop"]=JsonSerializer.SerializeToElement(crop.ToFilter());
                            variables["video.crop.w"]=JsonSerializer.SerializeToElement(crop.Width);
                            variables["video.crop.h"]=JsonSerializer.SerializeToElement(crop.Height);
                            variables["video.crop.x"]=JsonSerializer.SerializeToElement(crop.X);
                            variables["video.crop.y"]=JsonSerializer.SerializeToElement(crop.Y);
                            variables["video.crop.filter"]=JsonSerializer.SerializeToElement(crop.ToFilter());

                            if(MediaActions.Text(c,"processingMode","plan")=="plan")
                            {
                                if(variables.TryGetValue("plan.tool",out var selected) && selected.GetString()!="ffmpeg")
                                    throw new MediaToolException("outil du plan incompatible avec cette action",1);
                                var stepCfg=new Dictionary<string,JsonElement>(c) { ["cropFilter"]=JsonSerializer.SerializeToElement(crop.ToFilter()) };
                                context=context with { Plan=[..context.Plan,new RuntimePlanStep(id,stepCfg,node.Id)] };
                            }
                            else
                            {
                                var stepCfg=new Dictionary<string,JsonElement>(c) { ["cropFilter"]=JsonSerializer.SerializeToElement(crop.ToFilter()) };
                                await ApplyOutcomeAsync(await actions.FfmpegPlanAsync([new(id,stepCfg,node.Id)],current,directory,node.Id,client.NodeId,job.Token),node.Id);
                            }
                        }
                        else output="no-crop";
                    }
                    else if(id is "base-deinterlace-video" or "video-deinterlace")
                    {
                        await EnsureScanAsync();
                        var frames=int.Parse(MediaActions.Number(c,"detectionFrames",1000));
                        var thresholdPercent=double.Parse(MediaActions.Number(c,"interlaceThresholdPercent",10),System.Globalization.CultureInfo.InvariantCulture);
                        var algo=MediaActions.Text(c,"algorithm","bwdif");

                        var isInterlaced=await actions.DetectInterlacedAsync(current,directory,frames,thresholdPercent,job.Token);
                        if(isInterlaced)
                        {
                            output="deinterlaced";
                            variables["video.deinterlace"]=JsonSerializer.SerializeToElement(algo);

                            if(MediaActions.Text(c,"processingMode","plan")=="plan")
                            {
                                if(variables.TryGetValue("plan.tool",out var selected) && selected.GetString()!="ffmpeg")
                                    throw new MediaToolException("outil du plan incompatible avec cette action",1);
                                context=context with { Plan=[..context.Plan,new RuntimePlanStep(id,c,node.Id)] };
                            }
                            else
                            {
                                await ApplyOutcomeAsync(await actions.FfmpegPlanAsync([new(id,c,node.Id)],current,directory,node.Id,client.NodeId,job.Token),node.Id);
                            }
                        }
                        else output="progressive";
                    }
                    else if(id is "base-file-size-guard" or "video-file-size-guard")
                    {
                        await EnsureFilesAsync();
                        var currentInfo=new FileInfo(current);
                        var sourceInfo=new FileInfo(original);
                        var currentBytes=currentInfo.Exists?currentInfo.Length:0;
                        var sourceBytes=sourceInfo.Exists?sourceInfo.Length:0;
                        var minReduction=decimal.Parse(MediaActions.Number(c,"minReductionPercent",10),System.Globalization.CultureInfo.InvariantCulture);
                        var (reduced,percentChange)=FileSizeGuard.Evaluate(sourceBytes,currentBytes,minReduction);

                        variables["runtime.fileReductionPercent"]=JsonSerializer.SerializeToElement(percentChange);
                        variables["runtime.currentSizeBytes"]=JsonSerializer.SerializeToElement(currentBytes);
                        variables["runtime.sourceSizeBytes"]=JsonSerializer.SerializeToElement(sourceBytes);

                        if(reduced) output="reduced";
                        else
                        {
                            output="larger";
                            if(MediaActions.Text(c,"actionOnFailure","branch")=="revert")
                            {
                                File.Copy(original,current,true);
                                var sha=await SafeMediaPaths.DigestAsync(current,job.Token);
                                context=context with { CurrentSha256=sha,Extension=Path.GetExtension(original),CurrentMetadata=context.BeforeMetadata };
                                logger.LogInformation("Garde de taille {Node} : gain insuffisant ({Percent:F1}%), source d'origine restaurée.",node.Id,percentChange);
                            }
                        }
                    }
                    else if(id is "base-filter-languages" or "audio-filter-languages")
                    {
                        await EnsureScanAsync();
                        var outcome=await actions.FilterLanguagesAsync(c,context.CurrentMetadata!.Value,current,directory,node.Id,job.Token);
                        if(outcome.Primary is null) output="unchanged";
                        else
                        {
                            output="filtered";
                            await ApplyOutcomeAsync(outcome,node.Id);
                        }
                    }
                    else if(id is "base-remove-commentary" or "audio-remove-commentary")
                    {
                        await EnsureScanAsync();
                        var outcome=await actions.RemoveCommentaryAsync(c,context.CurrentMetadata!.Value,current,directory,node.Id,job.Token);
                        if(outcome.Primary is null) output="unchanged";
                        else
                        {
                            output="removed";
                            await ApplyOutcomeAsync(outcome,node.Id);
                        }
                    }
                    else if(id=="base-execute-processing")
                    {
                        await EnsureScanAsync();
                        if(context.Plan.Count==0 && !MediaActions.Flag(c,"forceProcessing")) output="skipped";
                        else
                        {
                            var plan=context.Plan.ToList();
                            if(plan.Count==0 && variables.TryGetValue("plan.tool",out var selectedTool) && selectedTool.GetString()=="mkvtoolnix") plan.Add(new("base-remux-matroska",new Dictionary<string,JsonElement>()));
                            if(plan.Count==0) plan.Add(new("base-remux-media",new Dictionary<string,JsonElement>{["container"]=JsonSerializer.SerializeToElement(context.Extension.TrimStart('.'))}));
                            var index=0;
                            while(index<plan.Count)
                            {
                                if(IsFfmpegPlan(plan[index].ComponentId))
                                {
                                    var batch=new List<RuntimePlanStep>(); while(index<plan.Count && IsFfmpegPlan(plan[index].ComponentId)) batch.Add(plan[index++]);
                                    var outcome=await actions.FfmpegPlanAsync(batch,current,directory,node.Id+index,client.NodeId,job.Token,!variables.TryGetValue("plan.preserveUnchangedTracks",out var preserve)||preserve.GetBoolean());
                                    await ApplyOutcomeAsync(outcome,node.Id);
                                }
                                else { var step=plan[index++]; await ApplyOutcomeAsync(await actions.ExecuteAsync(step.ComponentId,step.Configuration,current,directory,node.Id+index,ExtraAsync,client.NodeId,job.Token),node.Id); }
                            }
                            context=context with { Plan=[] };
                        }
                    }
                    else
                    {
                        await EnsureScanAsync();
                        if(MediaActions.Text(c,"processingMode","direct")=="plan")
                        {
                            if(variables.TryGetValue("plan.tool",out var selected) && (IsFfmpegPlan(id)!=(selected.GetString()=="ffmpeg")))
                                throw new MediaToolException("outil du plan incompatible avec cette action",1);
                            context=context with { Plan=[..context.Plan,new RuntimePlanStep(id,c,node.Id)] };
                        }
                        else
                        {
                            var outcome=await actions.ExecuteAsync(id,c,current,directory,node.Id,ExtraAsync,client.NodeId,job.Token);
                            if(outcome.Value is { } value) variables[MediaActions.Text(c,"resultVariable","result")]=value;
                            await ApplyOutcomeAsync(outcome,node.Id);
                            if(MediaActions.Flag(c,"refreshMetadata")) context=context with { CurrentMetadata=await scanner.ScanAsync(current,job.Token) };
                        }
                    }
                    if(materialized && await SafeMediaPaths.DigestAsync(current,job.Token)!=context.CurrentSha256) {
                        await EnsureScanAsync(); await ApplyOutcomeAsync(new(current,[]),node.Id);
                    }
                }
                catch(AudioRuleException exception)
                {
                    logger.LogInformation("Règles audio {Node} non applicables : {Detail}",node.Id,exception.Message);
                    output="error";
                }
                catch(SubtitleException exception)
                {
                    logger.LogInformation("Sous-titres ou polices {Node} non applicables : {Detail}",node.Id,exception.Message);
                    output="error";
                }
                catch(ArgumentException exception) when(node.Component.PluginId=="base" && node.Component.ComponentId is "base-disk-lock" or "base-disk-unlock")
                {
                    logger.LogInformation("Verrou de disque {Node} invalide : {Detail}",node.Id,exception.Message);
                    output="error";
                }
                catch(ConditionEvaluationException exception)
                {
                    logger.LogInformation("Condition {Node} impossible à évaluer : {Detail}",node.Id,exception.Message);
                    output="error";
                }
                catch(MediaToolException exception)
                {
                    logger.LogInformation("Échec de l’outil pendant l’étape {Node}: {Detail}",node.Id,exception.Message);
                    // Une sortie « error » non reliée n'existe pas pour le serveur : l'échec est terminal (0015) au lieu d'un checkpoint refusé qui rejouerait l'exécution.
                    if(node.Component.PluginId is not ("base" or "conditions") || FlowPath.NextNodeId(lease.Flow,node.Id,"error") is null) throw;
                    output="error";
                    await client.DownloadAsync(lease.ExecutionId,lease.Epoch,context.CurrentSha256,current,job.Token);
                }
                context=context with { Variables=new Dictionary<string,JsonElement>(variables) };
                next=FlowPath.NextNodeId(lease.Flow,node.Id,output);
                if(next is null) wait=null;
                var checkpoint=new RuntimeCheckpoint(node.Id,started.InvocationId,output,next,context,terminal,handoff,wait);
                var eventId=Guid.NewGuid();
                await leaseGate.WaitAsync(job.Token);
                try
                {
                    var request=new CompleteRuntimeStep(lease.Epoch,eventId,Volatile.Read(ref revision),checkpoint,
                        commandCapture.ForStep(node.Id,started.InvocationId));
                    for(var retry=0;;retry++)
                    {
                        try
                        {
                            await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/steps/complete",request,job.Token);
                            commandCapture.MarkPersisted(node.Id,started.InvocationId);
                            break;
                        }
                        catch(HttpRequestException exception) when((handoff is not null || wait is not null) && retry<2
                            && (exception.StatusCode is null || (int)exception.StatusCode>=500))
                        { await Task.Delay(TimeSpan.FromSeconds(1),job.Token); }
                    }
                    if(handoff is not null || wait is not null)
                    {
                        logger.LogInformation(wait is not null?"Exécution {ExecutionId} mise en attente par le bloc {Node}.":"Exécution {ExecutionId} cédée après le bloc {Node}.",lease.ExecutionId,node.Id);
                        job.Cancel(); return;
                    }
                }
                finally { leaseGate.Release(); }
                await RenewAsync(job.Token); lastOutput=output;
            }
            await EnsureScanAsync(); context=context with { Variables=variables };
            var failed=terminal=="failed" || terminal is null && lastOutput=="error";
            if(context.Plan.Count>0 && !failed) throw new IOException("Le flow se termine avec un plan non exécuté.");

            var changed=!failed && terminal is not("unchanged" or "deferred") && context.CurrentSha256!=sourceSha;
            await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/finish",new FinishRuntimeExecution(lease.Epoch,Guid.NewGuid(),Volatile.Read(ref revision),failed?"failed":terminal=="deferred"?"deferred":"succeeded",
                failed?"Le flow a atteint une fin en échec.":"Flow terminé ; scans média réalisés.",changed?context.CurrentSha256:null,changed?context.Extension:null,context.BeforeMetadata,context.CurrentMetadata,terminal=="deferred"?(variables.TryGetValue("runtime.deferMinutes",out var delay)?delay.GetInt32():2):null,
                commandCapture.Pending()),job.Token);
            if(changed)
            {
                while(true)
                {
                    var status=await client.ExecutionAsync(lease.ExecutionId,job.Token); if(status.State is "succeeded" or "failed" or "cancelled") break;
                    await Task.Delay(TimeSpan.FromSeconds(2),job.Token);
                }
            }
        }
        catch(OperationCanceledException) when(job.IsCancellationRequested)
        {
            if(cancelled && !lost) await FailureAsync("cancelled","Annulation confirmée après arrêt des processus.");
        }
        catch(HttpRequestException exception) when(exception.StatusCode==System.Net.HttpStatusCode.Conflict)
        {
            // Un 409 peut signaler un checkpoint ou une complétion refusé par le contrat alors que le bail
            // est toujours valide. Traiter ce cas comme une perte de bail ferait reboucler indéfiniment
            // les médias invalides. FailureAsync renouvelle d'abord le bail et confirme un échec terminal ;
            // si le bail a réellement changé d'époque, le renouvellement sera refusé et l'ancien worker
            // ne pourra rien modifier.
            logger.LogWarning(exception,"Complétion de l’exécution {ExecutionId} refusée par le cluster ; tentative de clôture en échec.",lease.ExecutionId);
            lost=true;
            await FailureAsync("failed","Le cluster a refusé une étape ou une complétion de l’exécution.");
        }
        catch(HttpRequestException exception) when(exception.StatusCode is null || (int)exception.StatusCode>=500)
        {
            // Un 409 peut aussi être un checkpoint refusé alors que le bail est intact : rejouer ne le rendrait pas valide.
            var refused=false;
            if(exception.StatusCode==System.Net.HttpStatusCode.Conflict)
            {
                try
                {
                    using var probe=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew",new RuntimeFence(lease.Epoch),probe.Token);
                    refused=true;
                }
                catch(Exception) { }
            }
            if(refused)
            {
                logger.LogWarning("Exécution {ExecutionId} : le serveur a refusé une étape alors que le bail est valide ; échec.",lease.ExecutionId);
                await FailureAsync("failed","Le serveur a refusé le résultat d’une étape ; l’exécution est en échec.");
            }
            else
            {
                lost=true;
                logger.LogWarning(exception,"Exécution {ExecutionId} interrompue pendant un échange HTTP ({StatusCode}) ; reprise après expiration du bail.",lease.ExecutionId,exception.StatusCode);
            }
        }
        catch(Exception exception)
        {
            logger.LogWarning("Exécution {ExecutionId} interrompue : {Type}",lease.ExecutionId,exception.GetType().Name);
            if(!lost) await FailureAsync("failed",exception is MediaToolException?exception.Message:"L’exécution a échoué ; aucun remplacement de source n’a été effectué.");
        }
        finally { job.Cancel(); await renewal; }

        async Task FailureAsync(string state,string detail)
        {
            try
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var status=await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew",new RuntimeFence(lease.Epoch),timeout.Token);
                await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/finish",new FinishRuntimeExecution(lease.Epoch,Guid.NewGuid(),status!.Revision,state,detail,null,null,
                    Commands:commandCapture.Pending()),timeout.Token);
            }
            catch(Exception) { logger.LogWarning("Le résultat de l’exécution {ExecutionId} n’a pas pu être confirmé par le cluster.",lease.ExecutionId); }
        }
    }

    /// <summary>Évalue la condition de plugin d'une attente avec le contexte figé du checkpoint ; aucune étape n'est créée.</summary>
    private async Task ProbeAsync(RuntimeLease lease,RuntimeWaitProbe probe,RuntimeClient client,string directory,CancellationToken ct)
    {
        string? output=null;
        try
        {
            var node=lease.Flow.Nodes.Single(item=>item.Id==probe.BlockNodeId);
            var context=lease.Checkpoint?.Context??throw new InvalidOperationException("Sonde sans checkpoint.");
            var invocation=new FlowStepInvocation(1,Guid.NewGuid(),lease.ExecutionId,lease.Attempt,client.NodeId,lease.Epoch,node.Id,node.Component,node.Configuration,
                new(lease.SourceSha256,"current"+context.Extension,context.BeforeMetadata,context.CurrentMetadata,new Dictionary<string,JsonElement>(context.Variables),lease.MediaPath,lease.LibraryId),
                DateTimeOffset.UtcNow.AddSeconds(45),lease.PluginConfigurations?.GetValueOrDefault(node.Component.PluginId));
            var response=await plugins.ExecuteAsync(invocation,directory,ct);
            output=response.OutputId;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { return; }
        catch(Exception exception) { logger.LogInformation("Sonde d'attente {ExecutionId} sans résultat : {Type}.",lease.ExecutionId,exception.GetType().Name); }
        try { await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/wait/probe",new ReportWaitProbe(lease.Epoch,output),ct); }
        catch(HttpRequestException exception) { logger.LogInformation("Résultat de sonde non confirmé ({Status}) ; le bail expirera.",exception.StatusCode); }
    }

    private static bool IsFfmpegPlan(string id)=>FlowMediaValues.IsFfmpegPlan(id);
    private async Task<JsonElement> ScriptAsync(string id,IReadOnlyDictionary<string,JsonElement> config,object context,string directory,CancellationToken ct)
    {
        var python=id=="base-script" && MediaActions.Text(config,"language")=="python";
        var code=MediaActions.Text(config,"code");
        if(id=="base-condition") {
            var expression="(function(){ const context=JSON.parse("+JsonSerializer.Serialize(JsonSerializer.Serialize(context))+"); const variables=context.variables; return ("+MediaActions.Text(config,"expression")+"); })()";
            code="const vm=require('vm'); return {outputId: vm.runInNewContext("+JsonSerializer.Serialize(expression)+",Object.create(null),{timeout:5000,contextCodeGeneration:{strings:false,wasm:false}})?'yes':'no'};";
        }
        var file=Path.Combine(directory,"script-"+Guid.NewGuid().ToString("N")+(python?".py":".js"));
        var body=ScriptTools.Compose(python,id=="base-script" && MediaActions.Flag(config,"useTools"),code);
        await File.WriteAllTextAsync(file,body,ct);
        try
        {
            var executable=ProcessToolRunner.Find(python?"python3":"node")??throw new IOException("Interpréteur indisponible.");
            var result=await tools.RunAsync(executable,[file],directory,TimeSpan.FromSeconds(id=="base-condition"?30:int.Parse(MediaActions.Number(config,"timeoutSeconds",60))),ct,JsonSerializer.Serialize(context),1048576,true);
            if(result.ExitCode==79) throw new IOException("Isolation du processus indisponible.");
            if(result.ExitCode!=0) throw new MediaToolException(python?"python":"javascript",result.ExitCode);
            using var document=JsonDocument.Parse(result.Output); return document.RootElement.Clone();
        }
        finally { File.Delete(file); }
    }
}
