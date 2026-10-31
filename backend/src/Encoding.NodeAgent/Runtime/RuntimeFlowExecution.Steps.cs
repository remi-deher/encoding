using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.NodeAgent.Runtime;

internal sealed partial class RuntimeFlowExecution
{
    private sealed class StepContext(FlowNodeDefinition node, StartedRuntimeStep started)
    {
        public FlowNodeDefinition Node => node;
        public StartedRuntimeStep Started => started;
        public string Id { get; set; } = "";
        public IReadOnlyDictionary<string, JsonElement> Config { get; set; } = new Dictionary<string, JsonElement>();
        public string? Output { get; set; } = "success";
        public RuntimeHandoffSelector? Handoff { get; set; }
        public RuntimeWait? Wait { get; set; }
    }

    // Retourne false lorsque l'exécution a été cédée ou mise en attente : la clôture n'a alors pas lieu ici.
    private async Task<bool> RunStepsAsync()
    {
        while (next is not null)
        {
            job.Token.ThrowIfCancellationRequested(); var node = lease.Flow.Nodes.Single(n => n.Id == next);
            activeBlock = node.Id;
            var started = (await client.PostAsync<StartedRuntimeStep>($"executions/{lease.ExecutionId:D}/steps/start", new StartRuntimeStep(lease.Epoch, node.Id), job.Token))!;
            if (started.Completed is { } completed)
            {
                context = completed.Context; variables = new(context.Variables); next = completed.NextNodeId; terminal = completed.TerminalResult; lastOutput = completed.OutputId;
                current = directRead ? original : Path.Combine(directory, "current" + context.Extension); materialized = false; continue;
            }
            using var commandStep = commandCapture.BeginStep(node.Id, started.InvocationId);
            terminal = null;
            var step = new StepContext(node, started);
            await ExecuteStepAsync(step);
            if (await CompleteStepAsync(step)) return false;
        }
        return true;
    }

    private async Task ExecuteStepAsync(StepContext ctx)
    {
        var node = ctx.Node; var started = ctx.Started;
        try
        {
            var id = node.Component.ComponentId; var c = FlowOperationConfiguration.Resolve(id, node.Configuration); ctx.Id = id; ctx.Config = c;
            if (FfmpegBuilderPolicy.Active(variables) && id != FfmpegBuilderPolicy.Execute
                && !FfmpegBuilderPolicy.CanPass(node.Component.PluginId, id))
                throw new IOException("Fermez la préparation FFmpeg avant cette opération autonome.");
            // External metadata-only plugins receive no path to the direct source.
            if (directRead && !DirectMediaInspection.Allows(node.Component.PluginId, id, c, context.Plan.Count, FfmpegBuilderPolicy.Active(variables))
                && !(id is "base-audio-rules" or "audio-rules" or "audio-convert-by-rules" && AudioAlreadyCompliant(c))
                && !(await SourceAlreadyConformsAsync(node.Id, id, c)))
            {
                var launch = plugins.Installed.SingleOrDefault(plugin => plugin.Id == node.Component.PluginId && plugin.Version == node.Component.PluginVersion);
                var descriptor = launch is null ? null : (await plugins.DescribeAsync(launch,
                    job.Token)).Components.SingleOrDefault(component => component.Id == id && component.Version == node.Component.ComponentVersion);
                if (!DirectMediaInspection.AllowsMetadataOnly(descriptor)) await EnsureFilesAsync();
            }
            if (id.StartsWith("base-input-", StringComparison.Ordinal)) { if (id != "base-input-file") await EnsureScanAsync(); }
            else if (id == "base-variable")
            {
                variables[MediaActions.Text(c, "name")] = FlowVariableValue.Parse(MediaActions.Text(c, "value"));
            }
            else if (id == FfmpegBuilderPolicy.Start) await StartFfmpegBuilderAsync(ctx);
            else if (id == FfmpegBuilderPolicy.Execute) await ExecuteFfmpegBuilderAsync(ctx);
            else if (id == "base-prepare-processing") await PrepareProcessingAsync(ctx);
            else if (id == "base-complete-flow") { await EnsureScanAsync(); ctx.Output = null; terminal = MediaActions.Text(c, "result", "unchanged"); }
            else if (id == "base-defer-processing") { await EnsureScanAsync(); ctx.Output = null; terminal = "deferred";
                variables["runtime.deferMinutes"] = JsonSerializer.SerializeToElement(int.Parse(MediaActions.Number(c,
                "delayMinutes", 2))); }
            else if (id is "base-disk-lock" or "base-disk-unlock") await DiskLockAsync(ctx);
            else if (id is "base-script" or "base-condition") await RunScriptAsync(ctx);
            else if (node.Component.PluginId == "conditions" && node.Component.ComponentId == "conditions-match") await MatchConditionsAsync(ctx);
            else if (node.Component.PluginId == "conditions" && MediaConditionEvaluator.IsMediaCondition(node.Component.ComponentId))
            {
                if (context.CurrentMetadata is null) await EnsureScanAsync();
                ctx.Output = MediaConditionEvaluator.Evaluate(node.Component.ComponentId, c, context.CurrentMetadata);
            }
            else if (node.Component.PluginId == "probe" && id == MediaIntegrityPolicy.Component) await CheckMediaIntegrityAsync(ctx);
            else if (node.Component.PluginId is "base" or "probe" && id != "probe-validate-output"
                && (id.StartsWith("base-probe-", StringComparison.Ordinal) || id.StartsWith("probe-", StringComparison.Ordinal)
                    && !id.StartsWith("probe-inspect-", StringComparison.Ordinal) && id != "probe-export-report"))
            {
                await EnsureScanAsync();
                variables[MediaActions.Text(c, "resultVariable", "result")] = FlowMediaValues.Probe(id, context.CurrentMetadata!.Value, int.Parse(MediaActions.Number(c, "trackId")));
            }
            else if (node.Component.PluginId == "probe" && id == "probe-validate-output") await ValidateOutputAsync(ctx);
            else if (RuntimeWaitPolicy.IsWait(node)) { ctx.Wait = RuntimeWaitPolicy.Resolve(node); ctx.Output = "resumed"; }
            else if (RuntimeHandoffPolicy.IsHandoff(node)) await PrepareHandoffAsync(ctx);
            else if (node.Component.PluginId == "nodes" && node.Component.ComponentId == RuntimeRetryPolicy.ComponentId)
            {
                // This block declares an execution policy; retry decisions are made by the server after a lost lease.
            }
            else if (node.Component.PluginId == "nodes" && id == "nodes-library-access") await CheckLibraryAccessAsync(ctx);
            else if (node.Component.PluginId == "nodes"
                && (node.Component.ComponentId is "nodes-run-on-node" or "nodes-require-capability" or "nodes-require-direct-access"))
            {
                // Scheduling enforces these constraints before assigning the file to this worker.
            }
            else if (node.Component.PluginId == "ai" && id == "ai-metadata-enricher") await EnrichMetadataWithAiAsync(ctx);
            else if (node.Component.PluginId == "ai" && id == "ai-translate-subtitles") await TranslateSubtitlesWithAiAsync(ctx);
            else if (node.Component.PluginId is not ("base" or "video" or "audio" or "subtitles" or "mkv" or "images" or "probe" or "ai")) await RunExternalPluginAsync(ctx);
            else if (id == "base-publish-result") await PublishResultAsync(ctx);
            else if (id == "mkv-edit-in-place") await EditInPlaceAsync(ctx);
            else if (directRead && conformingNodes.Contains(node.Id)) ctx.Output = "unchanged";
            else if (id is "base-track-names" or "subtitles-track-names" || FlowOperationConfiguration.IsNamingOperation(id)) await ApplyTrackNamesAsync(ctx);
            else if (id is "base-subtitle-manage" or "subtitles-manage" || FlowOperationConfiguration.IsSubtitleOperation(id)) await ManageSubtitlesAsync(ctx);
            else if (id is "base-add-fonts" or "subtitles-add-fonts" or "base-assemble-media") await AddFontsOrAssembleAsync(ctx);
            else if (id is "base-audio-rules" or "audio-rules" or "audio-convert-by-rules") await ApplyAudioRulesAsync(ctx);
            else if (id is "base-auto-crop" or "video-auto-crop") await DetectAutoCropAsync(ctx);
            else if (id is "base-deinterlace-video" or "video-deinterlace") await DetectDeinterlaceAsync(ctx);
            else if (id is "base-file-size-guard" or "video-file-size-guard") await GuardFileSizeAsync(ctx);
            else if (id is "base-filter-languages" or "audio-filter-languages") await FilterLanguagesAsync(ctx);
            else if (id is "base-remove-commentary" or "audio-remove-commentary") await RemoveCommentaryAsync(ctx);
            else if (id == "base-execute-processing") await ExecuteProcessingAsync(ctx);
            else await ApplyGenericOperationAsync(ctx);
            // Planning can inspect metadata first. Materialize only after it finds real work.
            if (directRead && context.Plan.Count > 0) await EnsureFilesAsync();
            if (materialized && currentStamp != MediaFileStamp.Read(current))
            {
                await EnsureScanAsync(); await ApplyOutcomeAsync(new(current, []), node.Id);
            }
        }
        catch (AudioRuleException exception)
        {
            logger.LogInformation("Règles audio {Node} non applicables : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
        }
        catch (PublishResultException exception)
        {
            logger.LogWarning("Publication {Node} impossible : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
        }
        catch (InPlaceEditException exception)
        {
            logger.LogWarning("Modification sur place {Node} refusée : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
        }
        catch (SubtitleException exception)
        {
            logger.LogInformation("Sous-titres ou polices {Node} non applicables : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
        }
        catch (ArgumentException exception) when (node.Component.PluginId == "base" && node.Component.ComponentId is "base-disk-lock" or "base-disk-unlock")
        {
            logger.LogInformation("Verrou de disque {Node} invalide : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
        }
        catch (ConditionEvaluationException exception)
        {
            logger.LogInformation("Condition {Node} impossible à évaluer : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
        }
        catch (MediaToolException exception)
        {
            logger.LogInformation("Échec de l’outil pendant l’étape {Node}: {Detail}", node.Id, exception.Message);
            // Une sortie « error » non reliée n'existe pas pour le serveur : l'échec est terminal (0015) au lieu d'un checkpoint refusé qui rejouerait l'exécution.
            if (node.Component.PluginId is not ("base" or "conditions" or "probe") || FlowPath.NextNodeId(lease.Flow, node.Id, "error") is null) throw;
            ctx.Output = "error";
            await client.DownloadAsync(lease.ExecutionId, lease.Epoch, context.CurrentSha256, current, job.Token);
        }
    }

    // Retourne true lorsque le checkpoint a cédé ou suspendu l'exécution.
    private async Task<bool> CompleteStepAsync(StepContext ctx)
    {
        var node = ctx.Node; var started = ctx.Started;
        context = context with { Variables = new Dictionary<string, JsonElement>(variables) };
        next = FlowPath.NextNodeId(lease.Flow, node.Id, ctx.Output);
        if (next is null) ctx.Wait = null;
        var placementHandoff = false;
        if (ctx.Handoff is null && next is not null && placementPlan?.NodeAssignments?.TryGetValue(next, out var plannedWorker) == true
            && plannedWorker != client.NodeId)
        {
            if (directRead) await EnsureFilesAsync();
            ctx.Handoff = new RuntimeHandoffSelector("node", plannedWorker);
            placementHandoff = true;
        }
        if (ctx.Handoff is not null && directRead) await EnsureFilesAsync();
        context = context with { Variables = new Dictionary<string, JsonElement>(variables) };
        var checkpoint = new RuntimeCheckpoint(node.Id, started.InvocationId, ctx.Output, next, context, terminal, ctx.Handoff, ctx.Wait, placementHandoff);
        var eventId = Guid.NewGuid();
        await leaseGate.WaitAsync(job.Token);
        try
        {
            var request = new CompleteRuntimeStep(lease.Epoch, eventId, Volatile.Read(ref revision), checkpoint,
                commandCapture.ForStep(node.Id, started.InvocationId),
                node.Component.ComponentId == "ai-translate-subtitles" && node.Component.PluginId == "ai");
            for (var retry = 0; ; retry++)
            {
                try
                {
                    await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/steps/complete", request, job.Token);
                    commandCapture.MarkPersisted(node.Id, started.InvocationId);
                    break;
                }
                catch (HttpRequestException exception) when ((ctx.Handoff is not null || ctx.Wait is not null) && retry < 2
                    && (exception.StatusCode is null || (int)exception.StatusCode >= 500))
                { await Task.Delay(TimeSpan.FromSeconds(1), job.Token); }
            }
            if (ctx.Handoff is not null || ctx.Wait is not null)
            {
                logger.LogInformation(ctx.Wait is not null ? "Exécution {ExecutionId} mise en attente par le bloc {Node}." : "Exécution {ExecutionId} cédée après le bloc {Node}.",
                    lease.ExecutionId, node.Id);
                job.Cancel(); return true;
            }
        }
        finally { leaseGate.Release(); }
        await RenewAsync(job.Token); lastOutput = ctx.Output;
        return false;
    }

    private async Task StartFfmpegBuilderAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        if (FfmpegBuilderPolicy.Active(variables) || context.Plan.Count > 0)
            throw new IOException("Une préparation non exécutée ne peut pas être écrasée.");
        await EnsureScanAsync();
        variables[FfmpegBuilderPolicy.StateKey] = JsonSerializer.SerializeToElement(node.Id);
        variables["plan.tool"] = JsonSerializer.SerializeToElement("ffmpeg");
        variables["plan.preserveUnchangedTracks"] = JsonSerializer.SerializeToElement(MediaActions.Flag(c, "preserveUnchangedTracks", true));

    }

    private async Task ExecuteFfmpegBuilderAsync(StepContext ctx)
    {
        var node = ctx.Node;
        if (!FfmpegBuilderPolicy.Active(variables)) throw new IOException("Aucun builder FFmpeg ouvert.");
        await EnsureScanAsync();
        var inputContext = context;
        var input = current;
        try
        {
            if (context.Plan.Count == 0) ctx.Output = "skipped";
            else
            {
                if (context.Plan.Any(step => !IsFfmpegPlan(step.ComponentId)))
                    throw new IOException("Le builder contient une opération incompatible avec FFmpeg.");
                var outcome = await actions.FfmpegPlanAsync(context.Plan, current, directory, node.Id, client.NodeId, job.Token,
                    !variables.TryGetValue("plan.preserveUnchangedTracks", out var preserve) || preserve.GetBoolean(), knownMetadata: context.CurrentMetadata);
                if (outcome.Primary is null) ctx.Output = "skipped";
                await ApplyOutcomeAsync(outcome, node.Id);
            }
        }
        catch (Exception exception) when (exception is MediaToolException or IOException)
        {
            context = inputContext; current = input; currentStamp = materialized ? MediaFileStamp.Read(current) : null;
            ctx.Output = "error";
            logger.LogWarning("Échec du builder {Node}, entrée validée conservée : {Detail}", node.Id, exception.Message);
            if (FlowPath.NextNodeId(lease.Flow, node.Id, "error") is null) throw;
        }
        context = context with { Plan = [] };
        FfmpegBuilderPolicy.Close(variables);

    }

    private async Task PrepareProcessingAsync(StepContext ctx)
    {
        var c = ctx.Config;
        await EnsureScanAsync(); context = context with { Plan = [] };
        variables["plan.tool"] = JsonSerializer.SerializeToElement(MediaActions.Text(c, "tool", "ffmpeg"));
        variables["plan.preserveUnchangedTracks"] = JsonSerializer.SerializeToElement(MediaActions.Flag(c, "preserveUnchangedTracks", true));

    }

    private async Task DiskLockAsync(StepContext ctx)
    {
        var id = ctx.Id; var c = ctx.Config;
        var disk = DiskLockKey.Normalize(MediaActions.Text(c, "disk"));
        var request = new DiskLockRequest(lease.Epoch, disk);
        if (id == "base-disk-lock")
        {
            var taken = (await client.PostAsync<DiskLockResponse>($"executions/{lease.ExecutionId:D}/disk-locks/acquire", request, job.Token))!;
            ctx.Output = taken.Acquired ? "acquired" : "busy";
            variables["disk." + disk] = JsonSerializer.SerializeToElement(new { acquired = taken.Acquired, holder = taken.HolderExecutionId });
        }
        else await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/disk-locks/release", request, job.Token);

    }

    private async Task RunScriptAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureFilesAsync();
        if (id == "base-condition") await EnsureScanAsync();
        var result = await ScriptAsync(id, c, new { variables, metadata = context.CurrentMetadata, sourceMetadata = context.BeforeMetadata,
            currentFile = Path.GetFileName(current) }, directory, job.Token);
        ctx.Output = result.TryGetProperty("outputId", out var selected) ? selected.GetString() : "success";
        if (result.TryGetProperty("variables", out var changes)) foreach (var change in changes.EnumerateObject()) variables[change.Name] = change.Value.Clone();
        if (result.TryGetProperty("candidate", out var candidate)) { await EnsureScanAsync(); await ApplyOutcomeAsync(new(SafeMediaPaths.Resolve(directory, candidate.GetString()!), []), node.Id); }

    }

    private async Task MatchConditionsAsync(StepContext ctx)
    {
        var c = ctx.Config;
        var tree = ConditionTree.Parse(c["rules"], lease.Flow.MediaType);
        if (ConditionTree.RequiresMetadata(tree) && (context.CurrentMetadata is null || context.BeforeMetadata is null)) await EnsureScanAsync();
        ctx.Output = ConditionTree.Evaluate(tree, new(context with { Variables = variables }, lease.SourceNodeId, client.NodeId,
            lease.LibraryId, lease.MediaPath, sourceSha ?? "")) ? "yes" : "no";

    }

    private async Task CheckMediaIntegrityAsync(StepContext ctx)
    {
        var c = ctx.Config;
        await EnsureScanAsync();
        await EnsureFilesAsync();
        var mode = MediaActions.Text(c, "mode", "quick");
        var acceleration = MediaActions.Text(c, "acceleration", "auto");
        var cacheKey = "integrity." + context.CurrentSha256 + "." + mode + "." + acceleration;
        MediaIntegrityReport report;
        if (variables.TryGetValue(cacheKey, out var saved)
            && saved.Deserialize<MediaIntegrityReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { Output: "valid" } cached
            && await SafeMediaPaths.DigestAsync(current, job.Token) == context.CurrentSha256) report = cached;
        else report = await new MediaIntegrityChecker(tools, scanner).CheckAsync(current, context.CurrentSha256,
            lease.Flow.MediaType, mode, acceleration, await capabilities.DiscoverAsync(job.Token), job.Token);
        ctx.Output = report.Output;
        var serialized = JsonSerializer.SerializeToElement(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        variables[MediaIntegrityPolicy.ReceiptVariable] = serialized;
        if (report.Output == "valid")
        {
            variables[cacheKey] = serialized;
        }

    }

    private async Task ValidateOutputAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var report = await actions.ValidateOutputAsync(current, original, context.BeforeMetadata!.Value, context.CurrentMetadata!.Value, c, directory, job.Token);
        ctx.Output = report.GetProperty("valid").GetBoolean() ? "valid" : "invalid";
        variables["validation." + node.Id] = report;
        var issues = report.GetProperty("issues").EnumerateArray().Select(issue => issue.GetString()).Where(issue => !string.IsNullOrWhiteSpace(issue)).ToArray();
        logger.LogInformation("Contrôles après assemblage {Node} : {Result}{Issues}", node.Id, ctx.Output, issues.Length == 0 ? "" : " — " + string.Join("; ", issues));

    }

    private async Task PrepareHandoffAsync(StepContext ctx)
    {
        var node = ctx.Node;
        ctx.Handoff = RuntimeHandoffPolicy.Resolve(node, lease.PluginConfigurations?.GetValueOrDefault("nodes"));
        if (ctx.Handoff.Mode == "node" && ctx.Handoff.TargetNodeId == client.NodeId)
            throw new IOException("Le relais doit sélectionner un autre worker.");

    }

    private async Task CheckLibraryAccessAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        // Ce worker est le nœud d'accès du fichier et le monte directement (accès par transfert : « non »).
        var direct = lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null;
        if (direct && RuntimeHandoffPolicy.LibraryAccessNeedsEncoder(node))
            direct = Encoding.Contracts.Flows.MediaCodecPolicy.ProfileEncoderUsable(lease.Flow, (await capabilities.DiscoverAsync(job.Token)).FirstOrDefault(c => c.ToolId == "ffmpeg"));
        ctx.Output = direct ? "yes" : "no";

    }

    private async Task EnrichMetadataWithAiAsync(StepContext ctx)
    {
        var node = ctx.Node; var started = ctx.Started;
        await EnsureScanAsync();
        var response = await client.PostAsync<RuntimeAiMetadataEnrichmentResponse>(
            $"executions/{lease.ExecutionId:D}/ai/metadata/enrich",
            new RuntimeAiMetadataEnrichmentRequest(lease.Epoch, started.InvocationId, node.Id,
                Path.GetFileName(current), lease.Flow.MediaType, context.CurrentMetadata!.Value), job.Token);
        if (response is not { Success: true, Metadata: { } metadata }) ctx.Output = response?.ErrorCode ?? "error";
        else
        {
            var sidecarName = Path.GetFileNameWithoutExtension(current) + ".metadata.json";
            var sidecar = Path.Combine(directory, sidecarName);
            await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), job.Token);
            await ApplyOutcomeAsync(new(null, [sidecar]), node.Id);
        }

    }

    private async Task TranslateSubtitlesWithAiAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config; var started = ctx.Started;
        await EnsureScanAsync();
        try
        {
            var translate = new Func<int, IReadOnlyList<RuntimeAiSubtitleItem>, Task<RuntimeAiSubtitleTranslationResponse>>(
                async (batchIndex, items) => await client.PostAsync<RuntimeAiSubtitleTranslationResponse>(
                    $"executions/{lease.ExecutionId:D}/ai/subtitles/translate",
                    new RuntimeAiSubtitleTranslationRequest(lease.Epoch, started.InvocationId, node.Id, batchIndex, items), job.Token)
                    ?? new RuntimeAiSubtitleTranslationResponse(false, ErrorCode: "error"));
            var outcome = lease.Flow.MediaType == "video"
                ? await SubtitleTranslation.TranslateEmbeddedAsync(c, current, directory, node.Id, context.CurrentMetadata!.Value, tools, translate, job.Token)
                : await SubtitleTranslation.TranslateAsync(c, current, directory, node.Id, translate, job.Token);
            if (outcome.ErrorCode is { } aiError) ctx.Output = aiError;
            else if (outcome.File is { } translated) await ApplyOutcomeAsync(new(translated, []), node.Id);
            else ctx.Output = "unchanged";
        }
        catch (HttpRequestException exception) when ((int?)exception.StatusCode == 429)
        {
            ctx.Output = "rateLimit";
            logger.LogInformation("Le fournisseur IA a limité le bloc de traduction {Node}; le fichier source est conservé.", node.Id);
        }

    }

    private async Task RunExternalPluginAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config; var started = ctx.Started;
        await EnsureScanAsync();
        var invocation = new FlowStepInvocation(1, started.InvocationId, lease.ExecutionId, lease.Attempt, client.NodeId, lease.Epoch, node.Id, node.Component, c,
            new(sourceSha, directRead ? null : Path.GetFileName(current), context.BeforeMetadata, context.CurrentMetadata, variables, lease.MediaPath,
                lease.LibraryId), DateTimeOffset.UtcNow.AddHours(6),
            lease.PluginConfigurations?.GetValueOrDefault(node.Component.PluginId));
        var response = await plugins.ExecuteAsync(invocation, directory, job.Token);
        ctx.Output = response.OutputId; terminal = response.TerminalResult?.ToString().ToLowerInvariant();
        foreach (var change in response.VariableChanges) variables[change.Key] = change.Value;
        if (response.CandidateArtifactId is { } candidate) await ApplyOutcomeAsync(new(SafeMediaPaths.Resolve(directory, candidate), []), node.Id);

    }
}
