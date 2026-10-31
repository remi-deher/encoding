using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.NodeAgent.Runtime;

/// <summary>
/// Exécute un flow pour un bail donné : renouvellement du bail, préparation de la source, enchaînement des blocs,
/// puis clôture. L'état d'une exécution vit dans cette instance, jamais dans le singleton <see cref="RuntimeFlowRunner"/>.
/// </summary>
internal sealed partial class RuntimeFlowExecution(NodeAgentOptions options, MediaScanner scanner, MediaActions actions, ProcessToolRunner tools, PluginProcessHost plugins, PublicationGuardRunner guards, Encoding.NodeAgent.Services.IToolCapabilityProbe capabilities, LocalPublicationArtifactStore localPublicationArtifacts, ILogger logger, RuntimeLease lease, RuntimeClient client, CancellationToken stoppingToken) : IDisposable
{
    private readonly RuntimeCommandCapture commandCapture = RuntimeCommandCapture.Begin(lease.ExecutionId);
    private readonly CancellationTokenSource job = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    private readonly SemaphoreSlim leaseGate = new(1, 1);
    private readonly string directory = Path.Combine(options.DataDirectory, "executions", lease.ExecutionId.ToString("D"), lease.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private readonly HashSet<string> conformingNodes = new(StringComparer.Ordinal);
    private bool publicationConfirmed;
    private string? activeBlock;
    private int revision = lease.Revision;
    private bool cancelled;
    private bool lost;
    private string? sourceSha;
    private bool directRead;
    private string extension = "";
    private string original = "";
    private MediaFileStamp? observed;
    private RuntimeContext context = null!;
    private FlowPlacementPlan? placementPlan;
    private Dictionary<string, JsonElement> variables = new();
    private string? next;
    private string? terminal;
    private string? lastOutput;
    private string current = "";
    private bool materialized;
    private MediaFileStamp? currentStamp;

    public void Dispose()
    {
        job.Dispose();
        leaseGate.Dispose();
        commandCapture.Dispose();
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(directory);
        if (lease.Probe is { } wanted) { await ProbeAsync(lease, wanted, client, directory, stoppingToken); return; }
        var renewal = StartRenewal();
        try
        {
            await InitializeAsync();
            if (!await RunStepsAsync()) return;
            await FinishAsync();
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            if (cancelled && !lost) await FailureAsync("cancelled", "Annulation confirmée après arrêt des processus.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Un 409 peut signaler un checkpoint ou une complétion refusé par le contrat alors que le bail
            // est toujours valide. Traiter ce cas comme une perte de bail ferait reboucler indéfiniment
            // les médias invalides. FailureAsync renouvelle d'abord le bail et confirme un échec terminal ;
            // si le bail a réellement changé d'époque, le renouvellement sera refusé et l'ancien worker
            // ne pourra rien modifier.
            logger.LogWarning(exception, "Complétion de l’exécution {ExecutionId} refusée par le cluster ; tentative de clôture en échec.", lease.ExecutionId);
            lost = true;
            await FailureAsync("failed", "cluster_refused : Le cluster a refusé une étape ou une complétion de l’exécution.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null || (int)exception.StatusCode >= 500)
        {
            // Un 409 peut aussi être un checkpoint refusé alors que le bail est intact : rejouer ne le rendrait pas valide.
            var refused = false;
            if (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                try
                {
                    using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew", new RuntimeFence(lease.Epoch), probe.Token);
                    refused = true;
                }
                catch (Exception) { }
            }
            if (refused)
            {
                logger.LogWarning("Exécution {ExecutionId} : le serveur a refusé une étape alors que le bail est valide ; échec.", lease.ExecutionId);
                await FailureAsync("failed", "cluster_refused : Le serveur a refusé le résultat d’une étape ; l’exécution est en échec.");
            }
            else
            {
                lost = true;
                logger.LogWarning(exception, "Exécution {ExecutionId} interrompue pendant un échange HTTP ({StatusCode}) ; reprise après expiration du bail.", lease.ExecutionId, exception.StatusCode);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning("Exécution {ExecutionId} interrompue : {Type}", lease.ExecutionId, exception.GetType().Name);
            if (!lost)
            {
                var code = exception.Message.StartsWith("placement_impossible:", StringComparison.Ordinal) ? "placement_impossible" : exception switch
                {
                    LibraryAccessException or UnauthorizedAccessException => "permission_denied",
                    MediaToolException => "media_tool_failed",
                    OperationCanceledException or TimeoutException => "operation_timeout",
                    _ => lease.Flow.Nodes.FirstOrDefault(node => node.Id == activeBlock)?.Component.PluginId is "plex" or "sonarr" or "radarr" or "jellyfin" ? "integration_infrastructure_failed" : "execution_infrastructure_failed"
                };
                var detail = code == "placement_impossible" || exception is MediaToolException or FlowArtifactReferenceException or LibraryAccessException
                    ? exception.Message : "Vérifiez les outils, accès et intégrations de cette étape.";
                await FailureAsync("failed", $"{code} : bloc {activeBlock ?? "préparation"}. "
                    + (publicationConfirmed ? "Publication média confirmée ; échec d’une étape ultérieure. " : "Aucune nouvelle confirmation de publication obtenue. ") + detail);
            }
        }
        finally { job.Cancel(); await renewal; }
    }

    private Task StartRenewal()
    {
        return Task.Run(async () =>
        {
            var consecutiveFailures = 0;
            try
            {
                while (!job.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), job.Token);
                    try
                    {
                        await RenewAsync(job.Token);
                        consecutiveFailures = 0;
                    }
                    catch (OperationCanceledException) when (job.IsCancellationRequested) { break; }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        logger.LogWarning("Bail de l’exécution {ExecutionId} révoqué par le cluster (HTTP 409).", lease.ExecutionId);
                        lost = true;
                        job.Cancel();
                        break;
                    }
                    catch (Exception exception)
                    {
                        consecutiveFailures++;
                        logger.LogWarning("Renouvellement du bail de l’exécution {ExecutionId} en échec ({Failures} consécutifs) : {Type} {Message}.",
                            lease.ExecutionId, consecutiveFailures, exception.GetType().Name, exception.Message);
                        if (consecutiveFailures >= 5)
                        {
                            logger.LogError("Bail de l’exécution {ExecutionId} considéré comme perdu après 5 échecs consécutifs.", lease.ExecutionId);
                            lost = true;
                            job.Cancel();
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (job.IsCancellationRequested) { }
        }, CancellationToken.None);
    }

    private async Task InitializeAsync()
    {
        sourceSha = lease.SourceSha256;
        directRead = lease.DirectRead && lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null;
        while (sourceSha is null && !directRead)
        {
            var source = await client.GetAsync<RuntimeSourceState>($"executions/{lease.ExecutionId:D}/source?epoch={lease.Epoch}", job.Token);
            sourceSha = source.SourceSha256; if (sourceSha is null) await Task.Delay(TimeSpan.FromSeconds(2), job.Token);
        }
        extension = Path.GetExtension(lease.MediaPath); if (string.IsNullOrWhiteSpace(extension)) extension = ".mkv";
        original = directRead ? SafeMediaPaths.Resolve(lease.SourceRoot!, lease.MediaPath) : Path.Combine(directory, "source" + extension);
        if (lease.VerifyPublicationAccess && lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null)
            await LibraryAccessVerifier.VerifyPublicationAsync(lease.SourceRoot, job.Token, lease.VerifyReplacementAccess ? lease.MediaPath : null);
        observed = directRead ? MediaFileStamp.Read(original) : null;
        context = lease.Checkpoint?.Context ?? new(sourceSha ?? "", extension, new Dictionary<string, JsonElement>(), null, null, [],
            DirectSource: observed is null ? null : new(observed.Bytes, observed.LastWriteUtcTicks));
        placementPlan = lease.PlacementPlan;
        if (context.DirectSource is { } prior)
        {
            if (directRead) new MediaFileStamp(prior.Bytes, prior.LastWriteUtcTicks).Verify(original);
            else context = context with { CurrentSha256 = sourceSha!, DirectSource = null };
        }
        variables = new Dictionary<string, JsonElement>(context.Variables);
        publicationConfirmed = variables.ContainsKey("runtime.published");
        next = lease.Checkpoint?.NextNodeId;
        if (lease.Checkpoint is null) next = lease.EntryNodeId;
        terminal = lease.Checkpoint?.TerminalResult; lastOutput = lease.Checkpoint?.OutputId;
        current = directRead ? original : Path.Combine(directory, "current" + context.Extension);
        materialized = false;
        currentStamp = null;
        if (directRead && context.BeforeMetadata is null)
        {
            // Source lisible en place : le sondage ne coûte aucune copie, on publie donc les métadonnées d'origine tout de suite.
            try { observed!.Verify(original); context = context with { BeforeMetadata = await scanner.ScanAsync(original, job.Token) }; observed.Verify(original); }
            catch (Exception) when (!job.IsCancellationRequested) { context = context with { BeforeMetadata = null }; }
            await ReportSourceMetadataAsync();
        }
        if (placementPlan is not null)
        {
            placementPlan = await client.PostAsync<FlowPlacementPlan>($"executions/{lease.ExecutionId:D}/placement-plan", new RuntimeFence(lease.Epoch), job.Token) ?? placementPlan;
            if (placementPlan.Strategy == FlowPlacementPlan.StrategyImpossible)
                throw new IOException("placement_impossible: " + placementPlan.Reason);
        }
    }

    private async Task<RuntimeLeaseStatus> RenewAsync(CancellationToken ct)
    {
        await leaseGate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var status = (await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew", new RuntimeFence(lease.Epoch, commandCapture.ForHeartbeat()), timeout.Token))!;
            Volatile.Write(ref revision, status.Revision);
            if (status.CancelRequested) { cancelled = true; job.Cancel(); }
            return status;
        }
        finally { leaseGate.Release(); }
    }
    private async Task EnsureFilesAsync()
    {
        if (materialized) return;
        if (directRead)
        {
            observed!.Verify(original);
            await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/source/prepare", new RuntimeFence(lease.Epoch), job.Token);
            while (sourceSha is null)
            {
                sourceSha = (await client.GetAsync<RuntimeSourceState>($"executions/{lease.ExecutionId:D}/source?epoch={lease.Epoch}", job.Token)).SourceSha256;
                if (sourceSha is null) await Task.Delay(TimeSpan.FromMilliseconds(250), job.Token);
            }
            observed.Verify(original);
            directRead = false;
            original = Path.Combine(directory, "source" + extension);
            current = Path.Combine(directory, "current" + context.Extension);
            context = context with { CurrentSha256 = sourceSha, DirectSource = null };
        }
        await client.DownloadAsync(lease.ExecutionId, lease.Epoch, sourceSha!, original, job.Token);
        if (context.CurrentSha256 == sourceSha)
        {
            if (original != current && !File.Exists(current))
            {
                File.Copy(original, current, true);
            }
        }
        else await client.DownloadAsync(lease.ExecutionId, lease.Epoch, context.CurrentSha256, current, job.Token);
        foreach (var variable in variables.Where(pair => pair.Key.StartsWith("artifact.", StringComparison.Ordinal)))
        {
            var sha = variable.Value.GetProperty("sha256").GetString()!;
            var name = variable.Value.GetProperty("name").GetString()!;
            var artifactDirectory = Path.Combine(directory, "artifacts", sha); Directory.CreateDirectory(artifactDirectory);
            await client.DownloadAsync(lease.ExecutionId, lease.Epoch, sha, SafeMediaPaths.Resolve(artifactDirectory, Path.GetFileName(name)), job.Token);
        }
        materialized = true;
        currentStamp = MediaFileStamp.Read(current);
    }
    // Les métadonnées d'origine sont visibles dans l'activité dès le sondage, sans attendre la fin d'une étape.
    private async Task ReportSourceMetadataAsync()
    {
        if (context.BeforeMetadata is not { } metadata) return;
        try
        {
            placementPlan = await client.PostAsync<FlowPlacementPlan>($"executions/{lease.ExecutionId:D}/source-metadata", new ReportSourceMetadata(lease.Epoch, metadata), job.Token) ?? placementPlan;
        }
        catch (Exception) when (!job.IsCancellationRequested) { }
        if (placementPlan?.Strategy == FlowPlacementPlan.StrategyImpossible)
            throw new IOException("placement_impossible: " + placementPlan.Reason);
    }
    private bool AudioAlreadyCompliant(IReadOnlyDictionary<string, System.Text.Json.JsonElement> configuration)
    {
        if (context.BeforeMetadata is not { } metadata || context.Plan.Count > 0 || FfmpegBuilderPolicy.Active(variables)) return false;
        try { return AudioRulePlanner.Plan(metadata, AudioRuleOptions.From(configuration)).Steps("probe", AudioRuleOptions.From(configuration)).Count == 0; }
        catch (AudioRuleException) { return false; }
    }
    // Nommage et sous-titres : planification à blanc sur la source directe ; aucun téléchargement si rien ne change.
    private async Task<bool> SourceAlreadyConformsAsync(string nodeId, string id, IReadOnlyDictionary<string, System.Text.Json.JsonElement> configuration)
    {
        var naming = id is "base-track-names" or "subtitles-track-names" || FlowOperationConfiguration.IsNamingOperation(id);
        var subtitles = id is "base-subtitle-manage" or "subtitles-manage" || FlowOperationConfiguration.IsSubtitleOperation(id);
        if (!directRead || !(naming || subtitles) || context.BeforeMetadata is not { } metadata || context.Plan.Count > 0 || FfmpegBuilderPolicy.Active(variables)) return false;
        if (naming && MediaActions.Text(configuration, "mode", "apply") == "snapshot") return false;
        try
        {
            observed!.Verify(original);
            var outcome = naming
                ? await actions.TrackNamesAsync(configuration, metadata, variables.TryGetValue("tracks.origin", out var saved) ? saved : null, original, directory, nodeId, job.Token, planOnly: true)
                : await actions.SubtitleManageAsync(configuration, metadata, original, directory, nodeId, job.Token, planOnly: true);
            observed.Verify(original);
            if (outcome.NeedsChanges || outcome.Primary is not null) return false;
            conformingNodes.Add(nodeId); return true;
        }
        catch (Exception) when (!job.IsCancellationRequested) { return false; }
    }
    private async Task EnsureScanAsync()
    {
        if (directRead) observed!.Verify(original);
        else await EnsureFilesAsync();
        if (context.BeforeMetadata is null)
        {
            context = context with { BeforeMetadata = await scanner.ScanAsync(original, job.Token) };
            await ReportSourceMetadataAsync();
        }
        if (context.CurrentMetadata is null) context = context with { CurrentMetadata = current == original || context.CurrentSha256 == sourceSha ? context.BeforeMetadata : await scanner.ScanAsync(current, job.Token) };
        if (directRead) observed!.Verify(original);
    }
    private async Task<string> ExtraAsync(string path)
    {
        if (FlowArtifactReference.TryParse(path, out var reference))
        {
            await EnsureFilesAsync();
            var artifact = reference.Resolve(variables);
            var sha = artifact.GetProperty("sha256").GetString()!;
            var folder = Path.Combine(directory, "artifacts", sha); Directory.CreateDirectory(folder);
            var target = SafeMediaPaths.Resolve(folder, Path.GetFileName(artifact.GetProperty("name").GetString()!));
            if (!File.Exists(target)) await client.DownloadAsync(lease.ExecutionId, lease.Epoch, sha, target, job.Token);
            return target;
        }
        if (path.StartsWith(FlowArtifactReference.Prefix, StringComparison.Ordinal))
            throw new FlowArtifactReferenceException("Référence de fichier produit invalide.");
        path = path.Replace('\\', '/');
        RuntimeSourceState state;
        while (true)
        {
            state = await client.GetAsync<RuntimeSourceState>($"executions/{lease.ExecutionId:D}/source?epoch={lease.Epoch}", job.Token);
            if (state.Inputs?.TryGetValue(path, out _) == true) break;
            await Task.Delay(TimeSpan.FromSeconds(2), job.Token);
        }
        var file = Path.Combine(directory, "input-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16] + Path.GetExtension(path));
        await client.DownloadAsync(lease.ExecutionId, lease.Epoch, state.Inputs![path], file, job.Token); return file;
    }
    private async Task EnsurePublicationIntegrityAsync()
    {
        await EnsureFilesAsync();
        if (await SafeMediaPaths.DigestAsync(current, job.Token) != context.CurrentSha256)
            throw new IOException("Le résultat a changé après son contrôle ; publication refusée.");
        if (!MediaIntegrityPolicy.IsExplicit(lease.Flow))
        {
            await scanner.ValidateAsync(current, lease.Flow.MediaType, context.BeforeMetadata, job.Token);
            return;
        }
        if (!variables.TryGetValue(MediaIntegrityPolicy.ReceiptVariable, out var value)
            || value.Deserialize<MediaIntegrityReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) is not { Output: "valid" } receipt
            || receipt.Sha256 != context.CurrentSha256)
            throw new IOException("Contrôle média explicite manquant ou invalide pour le résultat courant.");
        await scanner.ValidateAsync(current, lease.Flow.MediaType, context.BeforeMetadata, job.Token);
    }
    private async Task ApplyOutcomeAsync(MediaActionOutcome outcome, string node)
    {
        var artifactIndex = 0;
        foreach (var additional in outcome.Additional)
        {
            var sha = await SafeMediaPaths.DigestAsync(additional, job.Token);
            await client.PutAsync(lease.ExecutionId, lease.Epoch, additional, sha, false, job.Token);
            var description = JsonSerializer.SerializeToElement(new { sha256 = sha, name = Path.GetFileName(additional) });
            variables["artifact." + node + "." + Path.GetFileName(additional)] = description;
            variables[new FlowArtifactReference(node, artifactIndex++).VariableKey] = description;
        }
        if (outcome.Primary is { } candidate)
        {
            var metadata = await scanner.ValidateAsync(candidate, lease.Flow.MediaType, context.BeforeMetadata, job.Token);
            var sha = await SafeMediaPaths.DigestAsync(candidate, job.Token);
            await client.PutAsync(lease.ExecutionId, lease.Epoch, candidate, sha, false, job.Token);
            current = candidate; context = context with { CurrentSha256 = sha, Extension = Path.GetExtension(candidate), CurrentMetadata = metadata };
            currentStamp = MediaFileStamp.Read(current);
        }
        if (outcome.VideoEncodings is { Count: > 0 })
            context = context with { VideoEncodings = [.. (context.VideoEncodings ?? []), .. outcome.VideoEncodings] };
    }
    private async Task FailureAsync(string state, string detail)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var status = await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew", new RuntimeFence(lease.Epoch), timeout.Token);
            await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/finish", new FinishRuntimeExecution(lease.Epoch, Guid.NewGuid(), status!.Revision, state, detail, null, null,
                Commands: commandCapture.Pending()), timeout.Token);
        }
        catch (Exception) { logger.LogWarning("Le résultat de l’exécution {ExecutionId} n’a pas pu être confirmé par le cluster.", lease.ExecutionId); }
    }

    private async Task FinishAsync()
    {
        await EnsureScanAsync(); context = context with { Variables = variables };
        if (directRead) observed!.Verify(original);
        var failed = terminal == "failed" || terminal is null && lastOutput == "error";
        if (context.Plan.Count > 0 && !failed) throw new IOException("Le flow se termine avec un plan non exécuté.");

        // Après une publication par bloc (ADR 0003), la référence est le fichier publié : la fin du flow ne republie pas.
        var publishedSha = variables.TryGetValue("runtime.published", out var publication) && publication.TryGetProperty("sha256", out var publishedDigest) ? publishedDigest.GetString() : null;
        var changed = !failed && terminal is not ("unchanged" or "deferred") && context.CurrentSha256 != (publishedSha ?? sourceSha ?? "");
        if (changed && publishedSha is not null) throw new IOException("Le média a été modifié après sa publication ; ce résultat ne peut pas être publié une seconde fois.");
        if (changed && variables.ContainsKey("runtime.inPlaceEdited")) throw new IOException("La source a été modifiée sur place ; un résultat publié ensuite ne correspondrait plus à sa version d’origine.");
        if (changed) await EnsurePublicationIntegrityAsync();
        if (changed && lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null)
            localPublicationArtifacts.Register(lease.ExecutionId, lease.Epoch, context.CurrentSha256, current);
        await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/finish", new FinishRuntimeExecution(lease.Epoch, Guid.NewGuid(), Volatile.Read(ref revision), failed ? "failed" : terminal == "deferred" ? "deferred" : "succeeded",
            failed ? "Le flow a atteint une fin en échec." : "Flow terminé ; scans média réalisés.", changed ? context.CurrentSha256 : null, changed ? context.Extension : null, context.BeforeMetadata, context.CurrentMetadata, terminal == "deferred" ? (variables.TryGetValue("runtime.deferMinutes", out var delay) ? delay.GetInt32() : 2) : null,
            commandCapture.Pending()), job.Token);
        if (changed)
        {
            while (true)
            {
                var status = await client.ExecutionAsync(lease.ExecutionId, job.Token); if (status.State is "succeeded" or "failed" or "cancelled") break;
                await Task.Delay(TimeSpan.FromSeconds(2), job.Token);
            }
        }
    }


    /// <summary>Évalue la condition de plugin d'une attente avec le contexte figé du checkpoint ; aucune étape n'est créée.</summary>
    private async Task ProbeAsync(RuntimeLease lease, RuntimeWaitProbe probe, RuntimeClient client, string directory, CancellationToken ct)
    {
        string? output = null;
        try
        {
            var node = lease.Flow.Nodes.Single(item => item.Id == probe.BlockNodeId);
            var context = lease.Checkpoint?.Context ?? throw new InvalidOperationException("Sonde sans checkpoint.");
            var invocation = new FlowStepInvocation(1, Guid.NewGuid(), lease.ExecutionId, lease.Attempt, client.NodeId, lease.Epoch, node.Id, node.Component, node.Configuration,
                new(lease.SourceSha256, "current" + context.Extension, context.BeforeMetadata, context.CurrentMetadata, new Dictionary<string, JsonElement>(context.Variables), lease.MediaPath, lease.LibraryId),
                DateTimeOffset.UtcNow.AddSeconds(45), lease.PluginConfigurations?.GetValueOrDefault(node.Component.PluginId));
            var response = await plugins.ExecuteAsync(invocation, directory, ct);
            output = response.OutputId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception exception) { logger.LogInformation("Sonde d'attente {ExecutionId} sans résultat : {Type}.", lease.ExecutionId, exception.GetType().Name); }
        try { await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/wait/probe", new ReportWaitProbe(lease.Epoch, output), ct); }
        catch (HttpRequestException exception) { logger.LogInformation("Résultat de sonde non confirmé ({Status}) ; le bail expirera.", exception.StatusCode); }
    }

    /// <summary>Dernière ligne non vide de la sortie d'erreur d'un script (secrets masqués, 300 caractères au plus), pour que l'échec dise pourquoi.</summary>
    private static string LastErrorLine(string error)
    {
        var line = error.Split('\n').Select(item => item.Trim()).LastOrDefault(item => item.Length > 0 && !item.StartsWith("[tools]", StringComparison.Ordinal)) ?? "";
        line = RuntimeCommandCapture.Redact(line);
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static bool IsFfmpegPlan(string id) => FlowMediaValues.IsFfmpegPlan(id);
    private async Task<JsonElement> ScriptAsync(string id, IReadOnlyDictionary<string, JsonElement> config, object context, string directory, CancellationToken ct)
    {
        var python = id == "base-script" && MediaActions.Text(config, "language") == "python";
        var code = MediaActions.Text(config, "code");
        if (id == "base-condition")
        {
            var expression = "(function(){ const context=JSON.parse(" + JsonSerializer.Serialize(JsonSerializer.Serialize(context)) + "); const variables=context.variables; return (" + MediaActions.Text(config, "expression") + "); })()";
            code = "const vm=require('vm'); return {outputId: vm.runInNewContext(" + JsonSerializer.Serialize(expression) + ",Object.create(null),{timeout:5000,contextCodeGeneration:{strings:false,wasm:false}})?'yes':'no'};";
        }
        var file = Path.Combine(directory, "script-" + Guid.NewGuid().ToString("N") + (python ? ".py" : ".js"));
        var body = ScriptTools.Compose(python, id == "base-script" && MediaActions.Flag(config, "useTools"), code);
        await File.WriteAllTextAsync(file, body, ct);
        try
        {
            var executable = ProcessToolRunner.Find(python ? "python3" : "node") ?? throw new IOException("Interpréteur indisponible.");
            var result = await tools.RunAsync(executable, [file], directory, TimeSpan.FromSeconds(id == "base-condition" ? 30 : int.Parse(MediaActions.Number(config, "timeoutSeconds", 60))), ct, JsonSerializer.Serialize(context), 1048576, true);
            if (result.ExitCode == 79) throw new IOException("Isolation du processus indisponible.");
            if (result.ExitCode != 0) throw new MediaToolException(python ? "python" : "javascript", result.ExitCode, LastErrorLine(result.Error));
            using var document = JsonDocument.Parse(result.Output); return document.RootElement.Clone();
        }
        finally { File.Delete(file); }
    }
}
