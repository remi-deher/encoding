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
                var descriptor = launch is null ? null : (await plugins.DescribeAsync(launch, job.Token)).Components.SingleOrDefault(component => component.Id == id && component.Version == node.Component.ComponentVersion);
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
            else if (id == "base-defer-processing") { await EnsureScanAsync(); ctx.Output = null; terminal = "deferred"; variables["runtime.deferMinutes"] = JsonSerializer.SerializeToElement(int.Parse(MediaActions.Number(c, "delayMinutes", 2))); }
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
                && (id.StartsWith("base-probe-", StringComparison.Ordinal) || id.StartsWith("probe-", StringComparison.Ordinal) && !id.StartsWith("probe-inspect-", StringComparison.Ordinal) && id != "probe-export-report"))
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
                logger.LogInformation(ctx.Wait is not null ? "Exécution {ExecutionId} mise en attente par le bloc {Node}." : "Exécution {ExecutionId} cédée après le bloc {Node}.", lease.ExecutionId, node.Id);
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
        var result = await ScriptAsync(id, c, new { variables, metadata = context.CurrentMetadata, sourceMetadata = context.BeforeMetadata, currentFile = Path.GetFileName(current) }, directory, job.Token);
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
            new(sourceSha, directRead ? null : Path.GetFileName(current), context.BeforeMetadata, context.CurrentMetadata, variables, lease.MediaPath, lease.LibraryId), DateTimeOffset.UtcNow.AddHours(6),
            lease.PluginConfigurations?.GetValueOrDefault(node.Component.PluginId));
        var response = await plugins.ExecuteAsync(invocation, directory, job.Token);
        ctx.Output = response.OutputId; terminal = response.TerminalResult?.ToString().ToLowerInvariant();
        foreach (var change in response.VariableChanges) variables[change.Key] = change.Value;
        if (response.CandidateArtifactId is { } candidate) await ApplyOutcomeAsync(new(SafeMediaPaths.Resolve(directory, candidate), []), node.Id);

    }

    private async Task PublishResultAsync(StepContext ctx)
    {
        var node = ctx.Node;
        await EnsureScanAsync();
        if (variables.ContainsKey("runtime.published")) throw new PublishResultException("Le résultat a déjà été publié par un bloc de ce flow.");
        if (context.Plan.Count > 0) throw new PublishResultException("Le plan de traitement n'a pas été exécuté : placez la publication après « Exécuter le traitement ».");
        if (context.CurrentSha256 == sourceSha) ctx.Output = "unchanged";
        else
        {
            await EnsurePublicationIntegrityAsync();
            var playback = await guards.CheckForStepAsync(lease.ExecutionId, lease.Epoch, lease.MediaPath, lease.LibraryId, lease.Guards, true, job.Token);
            if (!playback.Allowed) throw new PublishResultException(playback.Detail ?? "Lecture en cours ; le résultat n'a pas été publié.");
            // Rejouable après une reprise (ADR 0004) : une publication déjà demandée ou faite pour ce même résultat est adoptée, jamais refaite.
            var already = await client.PublicationStateAsync(lease.ExecutionId, lease.Epoch, job.Token);
            if (already.Requested && already.Sha256 != context.CurrentSha256) throw new PublishResultException("Un autre résultat a déjà été publié pour cette exécution.");
            if (lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null)
                localPublicationArtifacts.Register(lease.ExecutionId, lease.Epoch, context.CurrentSha256, current);
            if (!already.Requested)
                await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/finish", new FinishRuntimeExecution(lease.Epoch, Guid.NewGuid(), Volatile.Read(ref revision), "succeeded",
                    "Résultat validé ; publication demandée par le flow.", context.CurrentSha256, context.Extension, context.BeforeMetadata, context.CurrentMetadata, null, commandCapture.Pending(), true), job.Token);
            RuntimePublicationState published;
            while (!(published = await client.PublicationStateAsync(lease.ExecutionId, lease.Epoch, job.Token)).Published) await Task.Delay(TimeSpan.FromSeconds(2), job.Token);
            publicationConfirmed = true;
            variables["runtime.published"] = JsonSerializer.SerializeToElement(new { path = published.Path, sha256 = context.CurrentSha256 });
            await RenewAsync(job.Token);
            logger.LogInformation("Publication {Node} confirmée : {Path}.", node.Id, published.Path);
            ctx.Output = "published";
        }

    }

    private async Task EditInPlaceAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        if (lease.SourceNodeId != client.NodeId || lease.SourceRoot is null)
            throw new InPlaceEditException("La modification sur place exige un worker avec accès direct à la bibliothèque : placez « Exiger un accès direct » (mode strict) avant ce bloc.");
        if (context.Plan.Count > 0 || context.CurrentSha256 != sourceSha || variables.ContainsKey("runtime.inPlaceEdited"))
            throw new InPlaceEditException("La modification sur place doit précéder tout autre bloc qui modifie le média, et ne peut être utilisée qu'une fois par flow.");
        var edit = InPlaceTrackEdit.From(c);
        // Lecture en cours (Plex) : attendre sa fin ; la garde est ensuite revérifiée juste avant l'écriture.
        var playbackWait = await guards.CheckForStepAsync(lease.ExecutionId, lease.Epoch, lease.MediaPath, lease.LibraryId, lease.Guards, true, job.Token);
        if (!playbackWait.Allowed) throw new InPlaceEditException(playbackWait.Detail ?? "Lecture en cours ; la source n'a pas été modifiée.");
        var result = await new InPlaceMkvEditor(tools).ApplyAsync(lease.SourceRoot, Path.Combine(options.DataDirectory, "media-locks"), lease.ExecutionId, lease.MediaPath, sourceSha,
            edit.Arguments, edit.AlreadyApplied, edit.Verify, directory, MediaActions.Flag(c, "keepBackup"), () => !job.IsCancellationRequested, job.Token,
            async token =>
            {
                if (lease.SourceWriteGuardsSupported) await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/source-write/authorize", new SourceWriteAuthorization(lease.Epoch, lease.SourceRoot), token);
                return (await guards.CheckForStepAsync(lease.ExecutionId, lease.Epoch, lease.MediaPath, lease.LibraryId, lease.Guards, false, token)) is { Allowed: false } last ? last.Detail ?? "Lecture en cours." : null;
            });
        logger.LogInformation("Modification sur place {Node} : {Detail}", node.Id, result.Detail);
        if (result.Status == "unchanged") ctx.Output = "unchanged";
        else variables["runtime.inPlaceEdited"] = JsonSerializer.SerializeToElement(result.NewSha256);

    }

    private async Task ApplyTrackNamesAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var outcome = await actions.TrackNamesAsync(c, context.CurrentMetadata!.Value, variables.TryGetValue("tracks.origin", out var saved) ? saved : null, current, directory, node.Id, job.Token);
        if (MediaActions.Text(c, "mode", "apply") == "snapshot") { if (outcome.Value is { } snapshot) variables["tracks.origin"] = snapshot; }
        else if (outcome.Primary is null) ctx.Output = "unchanged";
        else
        {
            if (outcome.Value is { ValueKind: JsonValueKind.Array } notes) foreach (var note in notes.EnumerateArray()) logger.LogInformation("Noms des pistes : {Note}", note.GetString());
            await ApplyOutcomeAsync(outcome with { Value = null }, node.Id);
        }

    }

    private async Task ManageSubtitlesAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var outcome = await actions.SubtitleManageAsync(c, context.CurrentMetadata!.Value, current, directory, node.Id, job.Token);
        if (outcome.Primary is null) ctx.Output = "unchanged";
        else
        {
            if (outcome.Value is { ValueKind: JsonValueKind.Array } notes) foreach (var note in notes.EnumerateArray()) logger.LogInformation("Sous-titres : {Note}", note.GetString());
            await ApplyOutcomeAsync(outcome with { Value = null }, node.Id);
        }

    }

    private async Task AddFontsOrAssembleAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureScanAsync();
        string? resourceFolder = null;
        if ((id != "base-assemble-media" || MediaActions.Flag(c, "addFonts")) && MediaActions.Text(c, "resource") is { Length: > 0 } resourceText)
        {
            if (!Encoding.Contracts.Flows.ResourceReference.TryParse(resourceText, out var resourceReference)) throw new SubtitleException("Référence de ressource de polices invalide.");
            try
            {
                var info = await client.ResourceVersionAsync(resourceReference, job.Token);
                resourceFolder = Path.Combine(directory, "resources", resourceReference.Id.ToString("N") + "-" + resourceReference.Version);
                await client.EnsureResourceAsync(info, ResourceCache.Root(options), resourceFolder, job.Token);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException) { throw new SubtitleException("Ressource de polices indisponible : " + exception.Message); }
        }
        var outcome = id == "base-assemble-media"
            ? await actions.AssembleAsync(c, context.CurrentMetadata!.Value, variables.TryGetValue("tracks.origin", out var origin) ? origin : null, current, directory, node.Id, job.Token, resourceFolder)
            : await actions.AddFontsAsync(c, current, directory, node.Id, job.Token, resourceFolder);
        if (outcome.Value is { ValueKind: JsonValueKind.Array } added) foreach (var note in added.EnumerateArray()) if (note.GetString() is { Length: > 0 } text) logger.LogInformation(id == "base-assemble-media" ? "Assemblage média : {Note}" : "{Note}", text);
        if (outcome.Primary is null) ctx.Output = "unchanged";
        else await ApplyOutcomeAsync(outcome with { Value = null }, node.Id);

    }

    private async Task ApplyAudioRulesAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var rules = AudioRuleOptions.From(c); var audioPlan = AudioRulePlanner.Plan(context.CurrentMetadata!.Value, rules);
        foreach (var decision in audioPlan.Decisions) logger.LogInformation("Audio piste {Track} : {Note}", decision.AudioIndex, decision.Note);
        var steps = audioPlan.Steps(node.Id, rules);
        if (steps.Count == 0) ctx.Output = "unchanged";
        else if (FfmpegBuilderPolicy.Active(variables) || MediaActions.Text(c, "processingMode", "plan") == "plan")
        {
            if (variables.TryGetValue("plan.tool", out var selected) && selected.GetString() != "ffmpeg")
                throw new MediaToolException("outil du plan incompatible avec cette action", 1);
            context = context with { Plan = [.. context.Plan, .. steps] };
        }
        else await ApplyOutcomeAsync(await actions.FfmpegPlanAsync(steps, current, directory, node.Id, client.NodeId, job.Token, knownMetadata: context.CurrentMetadata), node.Id);

    }

    private async Task DetectAutoCropAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureScanAsync();
        var duration = 0.0;
        if (context.CurrentMetadata is { } meta && meta.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.String && double.TryParse(durProp.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d))
            duration = d;

        var (w, h) = (0, 0);
        if (context.CurrentMetadata is { } m && m.TryGetProperty("streams", out var str))
        {
            foreach (var s in str.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var ctProp) && ctProp.GetString() == "video")
                {
                    if (s.TryGetProperty("width", out var wP)) w = wP.GetInt32();
                    if (s.TryGetProperty("height", out var hP)) h = hP.GetInt32();
                    break;
                }
            }
        }

        var samplePoints = int.Parse(MediaActions.Number(c, "samplePoints", 5));
        var sampleSecs = int.Parse(MediaActions.Number(c, "sampleDurationSeconds", 2));
        var threshold = int.Parse(MediaActions.Number(c, "threshold", 24));
        var roundMul = int.Parse(MediaActions.Number(c, "roundMultiple", 2));

        var crop = await actions.DetectCropAsync(current, directory, samplePoints, sampleSecs, threshold, roundMul, duration, job.Token);
        if (crop is not null && crop.IsSignificant(w, h))
        {
            ctx.Output = "cropped";
            variables["video.crop"] = JsonSerializer.SerializeToElement(crop.ToFilter());
            variables["video.crop.w"] = JsonSerializer.SerializeToElement(crop.Width);
            variables["video.crop.h"] = JsonSerializer.SerializeToElement(crop.Height);
            variables["video.crop.x"] = JsonSerializer.SerializeToElement(crop.X);
            variables["video.crop.y"] = JsonSerializer.SerializeToElement(crop.Y);
            variables["video.crop.filter"] = JsonSerializer.SerializeToElement(crop.ToFilter());

            if (FfmpegBuilderPolicy.Active(variables) || MediaActions.Text(c, "processingMode", "plan") == "plan")
            {
                if (variables.TryGetValue("plan.tool", out var selected) && selected.GetString() != "ffmpeg")
                    throw new MediaToolException("outil du plan incompatible avec cette action", 1);
                var stepCfg = new Dictionary<string, JsonElement>(c) { ["cropFilter"] = JsonSerializer.SerializeToElement(crop.ToFilter()) };
                context = context with { Plan = [.. context.Plan, new RuntimePlanStep(id, stepCfg, node.Id)] };
            }
            else
            {
                var stepCfg = new Dictionary<string, JsonElement>(c) { ["cropFilter"] = JsonSerializer.SerializeToElement(crop.ToFilter()) };
                await ApplyOutcomeAsync(await actions.FfmpegPlanAsync([new(id, stepCfg, node.Id)], current, directory, node.Id, client.NodeId, job.Token, knownMetadata: context.CurrentMetadata), node.Id);
            }
        }
        else ctx.Output = "no-crop";

    }

    private async Task DetectDeinterlaceAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureScanAsync();
        var frames = int.Parse(MediaActions.Number(c, "detectionFrames", 1000));
        var thresholdPercent = double.Parse(MediaActions.Number(c, "interlaceThresholdPercent", 10), System.Globalization.CultureInfo.InvariantCulture);
        var algo = MediaActions.Text(c, "algorithm", "bwdif");

        var isInterlaced = await actions.DetectInterlacedAsync(current, directory, frames, thresholdPercent, job.Token);
        if (isInterlaced)
        {
            ctx.Output = "deinterlaced";
            variables["video.deinterlace"] = JsonSerializer.SerializeToElement(algo);

            if (FfmpegBuilderPolicy.Active(variables) || MediaActions.Text(c, "processingMode", "plan") == "plan")
            {
                if (variables.TryGetValue("plan.tool", out var selected) && selected.GetString() != "ffmpeg")
                    throw new MediaToolException("outil du plan incompatible avec cette action", 1);
                context = context with { Plan = [.. context.Plan, new RuntimePlanStep(id, c, node.Id)] };
            }
            else
            {
                await ApplyOutcomeAsync(await actions.FfmpegPlanAsync([new(id, c, node.Id)], current, directory, node.Id, client.NodeId, job.Token, knownMetadata: context.CurrentMetadata), node.Id);
            }
        }
        else ctx.Output = "progressive";

    }

    private async Task GuardFileSizeAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureFilesAsync();
        var currentInfo = new FileInfo(current);
        var sourceInfo = new FileInfo(original);
        var currentBytes = currentInfo.Exists ? currentInfo.Length : 0;
        var sourceBytes = sourceInfo.Exists ? sourceInfo.Length : 0;
        var minReduction = decimal.Parse(MediaActions.Number(c, "minReductionPercent", 10), System.Globalization.CultureInfo.InvariantCulture);
        var (reduced, percentChange) = FileSizeGuard.Evaluate(sourceBytes, currentBytes, minReduction);

        variables["runtime.fileReductionPercent"] = JsonSerializer.SerializeToElement(percentChange);
        variables["runtime.currentSizeBytes"] = JsonSerializer.SerializeToElement(currentBytes);
        variables["runtime.sourceSizeBytes"] = JsonSerializer.SerializeToElement(sourceBytes);

        if (reduced) ctx.Output = "reduced";
        else
        {
            ctx.Output = "larger";
            if (MediaActions.Text(c, "actionOnFailure", "branch") == "revert")
            {
                File.Copy(original, current, true);
                var sha = MediaFileStamp.Read(current).ToDigest();
                context = context with { CurrentSha256 = sha, Extension = Path.GetExtension(original), CurrentMetadata = context.BeforeMetadata };
                logger.LogInformation("Garde de taille {Node} : gain insuffisant ({Percent:F1}%), source d'origine restaurée.", node.Id, percentChange);
            }
        }

    }

    private async Task FilterLanguagesAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var outcome = await actions.FilterLanguagesAsync(c, context.CurrentMetadata!.Value, current, directory, node.Id, job.Token);
        if (outcome.Primary is null) ctx.Output = "unchanged";
        else
        {
            ctx.Output = "filtered";
            await ApplyOutcomeAsync(outcome, node.Id);
        }

    }

    private async Task RemoveCommentaryAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var outcome = await actions.RemoveCommentaryAsync(c, context.CurrentMetadata!.Value, current, directory, node.Id, job.Token);
        if (outcome.Primary is null) ctx.Output = "unchanged";
        else
        {
            ctx.Output = "removed";
            await ApplyOutcomeAsync(outcome, node.Id);
        }

    }

    private async Task ExecuteProcessingAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        if (context.Plan.Count == 0 && !MediaActions.Flag(c, "forceProcessing")) ctx.Output = "skipped";
        else
        {
            var plan = context.Plan.ToList();
            if (plan.Count == 0 && variables.TryGetValue("plan.tool", out var selectedTool) && selectedTool.GetString() == "mkvtoolnix") plan.Add(new("base-remux-matroska", new Dictionary<string, JsonElement>()));
            if (plan.Count == 0) plan.Add(new("video-remux-media", new Dictionary<string, JsonElement> { ["container"] = JsonSerializer.SerializeToElement(context.Extension.TrimStart('.')) }));
            var index = 0;
            while (index < plan.Count)
            {
                if (IsFfmpegPlan(plan[index].ComponentId))
                {
                    var batch = new List<RuntimePlanStep>(); while (index < plan.Count && IsFfmpegPlan(plan[index].ComponentId)) batch.Add(plan[index++]);
                    var outcome = await actions.FfmpegPlanAsync(batch, current, directory, node.Id + index, client.NodeId, job.Token, !variables.TryGetValue("plan.preserveUnchangedTracks", out var preserve) || preserve.GetBoolean(), knownMetadata: context.CurrentMetadata);
                    await ApplyOutcomeAsync(outcome, node.Id);
                }
                else { var step = plan[index++]; await ApplyOutcomeAsync(await actions.ExecuteAsync(step.ComponentId, step.Configuration, current, directory, node.Id + index, ExtraAsync, client.NodeId, job.Token), node.Id); }
            }
            context = context with { Plan = [] };
        }

    }

    private async Task ApplyGenericOperationAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureScanAsync();
        IReadOnlyList<RuntimePlanStep> requested = [new(id, c, node.Id)];
        if (c.ContainsKey("trackSelection")) requested = MediaTrackSelection.Expand(requested, context.CurrentMetadata!.Value);
        if (id is "base-resize-video" or "video-resize")
        {
            var video = context.CurrentMetadata!.Value.GetProperty("streams").EnumerateArray().FirstOrDefault(stream => MediaTrackSelection.Text(stream, "codec_type") == "video");
            if (video.ValueKind == JsonValueKind.Object && !VideoResizePolicy.ShouldResize(c, MediaTrackSelection.Number(video, "width"), MediaTrackSelection.Number(video, "height"))) requested = [];
        }
        if (requested.Count == 0) ctx.Output = "unchanged";
        else if (FfmpegBuilderPolicy.IsPlanned(id, c, variables))
        {
            if (variables.TryGetValue("plan.tool", out var selected) && (IsFfmpegPlan(id) != (selected.GetString() == "ffmpeg")))
                throw new MediaToolException("outil du plan incompatible avec cette action", 1);
            context = context with { Plan = [.. context.Plan, .. requested] };
        }
        else
        {
            var outcome = await actions.ExecuteAsync(id, c, current, directory, node.Id, ExtraAsync, client.NodeId, job.Token, context.CurrentMetadata);
            if (outcome.Value is { } value) variables[MediaActions.Text(c, "resultVariable", "result")] = value;
            await ApplyOutcomeAsync(outcome, node.Id);
            if (outcome.Primary is null && MediaActions.Flag(c, "refreshMetadata")) context = context with { CurrentMetadata = await scanner.ScanAsync(current, job.Token) };
        }

    }
}
