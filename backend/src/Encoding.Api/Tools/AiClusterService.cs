using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Encoding.Contracts.Tools;
using Encoding.Api.Nodes;

namespace Encoding.Api.Tools;

public sealed record AiAssistantForwardConfirm(Guid OwnerId, bool IsAdmin, AiAssistantConfirmActionRequest Request);
public sealed record AiAssistantForwardConfirmResponse(bool Applied, string? Error = null, bool InvalidRequest = false);

/// <summary>A stable session coordinator owns writes; inference may move between connected peers without moving secrets.</summary>
public sealed class AiClusterService(AiClusterTransport transport, AiClusterStore store, DeviceCodeAuthService auth,
    AiSettingsService settings, AiClientFactory factory, IConfiguration configuration, LocalNodeRegistry localNode, AiQuotaService? quotaService = null, AiModelCatalogService? modelCatalog = null, IServiceProvider? services = null)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AiCompletionResult>>> running = new();
    private readonly object viewSync = new();
    private AiClusterView? cachedView;
    private DateTimeOffset cachedViewAt;
    private bool viewRefreshQueued;
    public async Task<AiNodeAvailability> LocalAsync(CancellationToken ct)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        preparation.CancelAfter(TimeSpan.FromSeconds(20));
        var token = preparation.Token;
        var providers = new List<string>();
        var models = new Dictionary<string, string?>(StringComparer.Ordinal);
        IReadOnlyList<AiModelCatalog>? catalogs = null;
        string? name = null;
        try
        {
            var tools = await auth.ListToolsAsync(token);
            providers.AddRange(tools.Where(t => t.IsInstalled && t.AuthStatus.State == "authorized").Select(t => t.Id));
            var local = await GetSettingsAsync(token);
            if (!local.CodexEnabled) providers.Remove("codex-cli");
            if (!local.ClaudeEnabled) providers.Remove("claude-cli");
            if (local.OllamaEnabled && !string.IsNullOrWhiteSpace(local.OllamaBaseUrl) && !string.IsNullOrWhiteSpace(local.OllamaModel))
            {
                providers.Add("ollama");
                models["ollama"] = local.OllamaModel;
            }
            if (local.OpenAiEnabled && !string.IsNullOrWhiteSpace(local.OpenAiBaseUrl) && !string.IsNullOrWhiteSpace(local.OpenAiModel) && !string.IsNullOrWhiteSpace(local.OpenAiApiKey))
            {
                providers.Add("openai");
                models["openai"] = local.OpenAiModel;
            }
            name = await localNode.ResolveDisplayNameAsync(token);
            if (modelCatalog is not null)
            {
                catalogs = providers.Select(provider => modelCatalog.ReadCachedOrRefresh(provider,
                    provider is "codex-cli" or "claude-cli" ? auth.ConnectionRevision(provider) : "http")).ToArray();
                var checkingCatalogs = tools.Where(tool => tool.IsInstalled && tool.AuthStatus.State == "checking"
                        && (tool.Id != "codex-cli" || local.CodexEnabled)
                        && (tool.Id != "claude-cli" || local.ClaudeEnabled))
                    .Select(tool => new AiModelCatalog(tool.Id, "loading", [], null, DateTimeOffset.UtcNow,
                        "Vérification de la connexion en cours."));
                catalogs = catalogs.Concat(checkingCatalogs).ToArray();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Preparation has one shared budget. Expired catalog reads are unavailable for this observation.
            catalogs = modelCatalog is null ? null : providers.Select(provider => new AiModelCatalog(provider, "unavailable", [], null,
                DateTimeOffset.UtcNow, "Préparation du catalogue dépassée." )).ToArray();
        }
        return new(transport.LocalId, name ?? configuration["Node:Name"] ?? Environment.MachineName,
            providers.Distinct(StringComparer.Ordinal).ToArray(), Models: models, Catalogs: catalogs);
    }
    public async Task<AiClusterView> ViewAsync(CancellationToken ct)
    {
        var nodes = new List<AiNodeAvailability> { await LocalAsync(ct) };
        var results = await Task.WhenAll((await transport.PeersAsync(ct)).Select(async url =>
        {
            try { return await transport.CallAsync<AiNodeAvailability>(url, "availability", new { }, TimeSpan.FromSeconds(15), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
        }));
        nodes.AddRange(results.OfType<AiNodeAvailability>().Where(n => n.NodeId != transport.LocalId));
        return new(nodes.DistinctBy(n => n.NodeId).ToArray());
    }
    public async Task<AiClusterView> CachedViewAsync(CancellationToken ct)
    {
        var local = await LocalAsync(ct);
        var peerUrls = await transport.PeersAsync(ct);
        AiClusterView? snapshot;
        var startRefresh = false;
        lock (viewSync)
        {
            snapshot = cachedView;
            if (!viewRefreshQueued && (snapshot is null || cachedViewAt.AddMinutes(1) <= DateTimeOffset.UtcNow))
            {
                viewRefreshQueued = true;
                startRefresh = true;
            }
        }
        if (startRefresh) _ = RefreshViewInBackground();
        var remoteNodes = snapshot?.Nodes.Where(node => node.NodeId != transport.LocalId).ToArray()
            ?? CreateLoadingPeerNodes(peerUrls);
        return new([local, .. remoteNodes]);
    }

    private async Task RefreshViewInBackground()
    {
        try
        {
            var snapshot = await ViewAsync(CancellationToken.None);
            lock (viewSync)
            {
                cachedView = snapshot;
                cachedViewAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception) { }
        finally { lock (viewSync) viewRefreshQueued = false; }
    }

    private static AiNodeAvailability[] CreateLoadingPeerNodes(IReadOnlyList<string> peerUrls)
    {
        var now = DateTimeOffset.UtcNow;
        var providers = new[] { "codex-cli", "claude-cli", "openai", "ollama" };
        return peerUrls.Select((url, index) => new AiNodeAvailability(
            new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(url)).AsSpan(0, 16)),
            $"Nœud distant {index + 1}", [], true,
            Catalogs: providers.Select(provider => new AiModelCatalog(provider, "loading", [], null, now,
                "Chargement des capacités annoncées par le nœud.")).ToArray())).ToArray();
    }
    public async Task<AiNodeModelCatalog> LocalModelsAsync(bool refresh, CancellationToken ct)
    {
        if (refresh && modelCatalog is not null)
            foreach (var provider in new[] { "codex-cli", "claude-cli", "openai", "ollama" }) modelCatalog.Invalidate(provider);
        var local = await LocalAsync(ct);
        return new(local.NodeId, local.Name, local.Catalogs ?? []);
    }
    public async Task<AiClusterModelCatalog> ModelsAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh)
        {
            var view = await CachedViewAsync(ct);
            return new(view.Nodes.Where(node => node.Reachable)
                .Select(node => new AiNodeModelCatalog(node.NodeId, node.Name, node.Catalogs ?? []))
                .ToArray(), await ModelPolicyAsync(ct));
        }
        var local = LocalModelsAsync(refresh, ct);
        var remote = await Task.WhenAll((await transport.PeersAsync(ct)).Select(async url =>
        {
            try { return await transport.CallAsync<AiNodeModelCatalog>(url, "models", new { Refresh = refresh }, TimeSpan.FromSeconds(20), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
        }));
        return new(new[] { await local }.Concat(remote.OfType<AiNodeModelCatalog>()).DistinctBy(n => n.NodeId).ToArray(), await ModelPolicyAsync(ct));
    }
    public async Task<AiModelPolicy> ModelPolicyAsync(CancellationToken ct) => new(
        (await store.ReadPrefixAsync<AiModelPolicyUpdate>("model-policy/", ct))
            .Where(p => !p.Enabled).Select(p => new AiDisabledModel(p.Provider, p.Model)).ToArray());
    public async Task<AiModelPolicy> UpdateModelPolicyAsync(AiModelPolicyUpdate update, CancellationToken ct)
    {
        if (update.Provider is not ("codex-cli" or "claude-cli" or "openai" or "ollama")
            || string.IsNullOrWhiteSpace(update.Model) || update.Model.Length > 256 || update.Model.Any(char.IsControl))
            throw new ArgumentException("Modèle ou fournisseur invalide.");
        // One replicated key per provider/model prevents concurrent edits to different models overwriting one another.
        var key = "model-policy/" + update.Provider + "/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(update.Model)));
        await store.SaveAsync(key, update, ct);
        return await ModelPolicyAsync(ct);
    }
    public static bool Supports(AiNodeAvailability node, string provider, AiPrompt prompt)
    {
        if (!node.Reachable || !node.Providers.Contains(provider) || prompt.NodeId is { } id && node.NodeId != id) return false;
        if (prompt.Model is null) return true;
        if (node.Catalogs is null) return node.Models?.GetValueOrDefault(provider) == prompt.Model && prompt.ReasoningEffort is null;
        return node.Catalogs.Any(c => c.Provider == provider && c.State == "available"
            && c.Models.Any(m => m.Id == prompt.Model && (prompt.ReasoningEffort is null || m.ReasoningEfforts.Contains(prompt.ReasoningEffort))));
    }
    public async Task<AiNodeQuotas> LocalQuotasAsync(CancellationToken ct)
    {
        var displayName = await localNode.ResolveDisplayNameAsync(ct) ?? configuration["Node:Name"] ?? Environment.MachineName;
        if (quotaService is null) return new(transport.LocalId, displayName, []);
        var quotas = await Task.WhenAll(new[] { "codex-cli", "claude-cli" }.Select(provider => quotaService.ReadAsync(provider, ct)));
        return new(transport.LocalId, displayName, quotas);
    }
    public async Task<AiClusterQuotas> QuotasAsync(CancellationToken ct)
    {
        var local = LocalQuotasAsync(ct);
        var peers = (await transport.PeersAsync(ct)).Select(async url =>
        {
            try { return await transport.CallAsync<AiNodeQuotas>(url, "quotas", new { }, TimeSpan.FromSeconds(20), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
        }).ToArray();
        var remote = await Task.WhenAll(peers);
        return new(new[] { await local }.Concat(remote.OfType<AiNodeQuotas>()).DistinctBy(n => n.NodeId).ToArray());
    }
    public async Task<object> DispatchAsync(string operation, JsonElement value, CancellationToken ct) => operation switch
    {
        "availability" => await LocalAsync(ct),
        "quotas" => await LocalQuotasAsync(ct),
        "assistant-cancel" => CancelForwardedAssistant(value.Deserialize<AiAssistantForwardCancel>()!),
        "assistant-message" => await ForwardAssistantAsync(value.Deserialize<AiAssistantForwardRequest>()!, ct),
        "assistant-business-operation" => await ForwardBusinessOperationAsync(value.Deserialize<AiAssistantForwardConfirm>()!, ct),
        "models" => await LocalModelsAsync(value.TryGetProperty("Refresh", out var refresh) && refresh.GetBoolean(), ct),
        "generate" => await GenerateAsync(value.Deserialize<AiGenerateRequest>()!.Provider, value.Deserialize<AiGenerateRequest>()!.Prompt, ct, allowModelChanges: value.Deserialize<AiGenerateRequest>()!.AllowModelChanges),
        "execute" => await ExecuteAsync(value.Deserialize<AiExecuteRequest>()!, ct),
        _ => throw new ArgumentException("Opération IA inconnue.")
    };
    private async Task<AiAssistantForwardResponse> ForwardAssistantAsync(AiAssistantForwardRequest request, CancellationToken ct)
    {
        if (request.OwnerId == Guid.Empty || services is null) throw new ArgumentException("Identité ou coordinateur indisponible.");
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, request.OwnerId.ToString()),
            new(System.Security.Claims.ClaimTypes.Role, request.IsAdmin ? "admin" : "viewer")], "cluster-ai"));
        try { return new(await services.GetRequiredService<AiAssistantService>().SendAsync(request.Request, user, ct)); }
        catch (ArgumentException ex) { return new(null, ex.Message, true); }
        catch (InvalidOperationException ex) { return new(null, ex.Message); }
    }
    private bool CancelForwardedAssistant(AiAssistantForwardCancel request)
    {
        if (request.OwnerId == Guid.Empty || services is null) return false;
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, request.OwnerId.ToString())], "cluster-ai"));
        return services.GetRequiredService<AiAssistantService>().CancelRequest(request.RequestId, user);
    }
    private async Task<AiAssistantForwardConfirmResponse> ForwardBusinessOperationAsync(AiAssistantForwardConfirm request, CancellationToken ct)
    {
        if (request.OwnerId == Guid.Empty || !request.IsAdmin || services is null)
            throw new ArgumentException("Identité ou autorisation indisponible.");
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, request.OwnerId.ToString()),
            new(System.Security.Claims.ClaimTypes.Role, "admin")], "cluster-ai"));
        try
        {
            var applied = await services.GetRequiredService<AiAssistantService>()
                .ConfirmBusinessOperationAsync(request.Request, user, ct);
            return new(applied);
        }
        catch (ArgumentException ex) { return new(false, ex.Message, true); }
        catch (InvalidOperationException ex) { return new(false, ex.Message); }
    }
    public async Task<AiSessionState?> GetSessionAsync(string id, CancellationToken ct)
    {
        _ = Owner(id);
        return await store.ReadAsync<AiSessionState>("session/" + id, ct);
    }

    public async Task CleanupRuntimeSubtitleSessionsAsync(Guid executionId, Guid? invocationId, CancellationToken ct)
    {
        var prefix = $"runtime-ai/subtitles/{executionId:N}/" + (invocationId is { } invocation ? $"{invocation:N}/" : "");
        string? cursor = null;
        while (true)
        {
            var page = await store.ReadPrefixPageAsync<string>(prefix, cursor, 500, ct);
            foreach (var (key, sessionId) in page)
            {
                await store.DeleteAsync(key["ai/".Length..], ct);
                if (!string.IsNullOrWhiteSpace(sessionId)) await store.DeleteAsync("session/" + sessionId, ct);
            }
            if (page.Count < 500) break;
            cursor = page[^1].Key;
        }
    }
    public async Task<AiSessionPage> ListSessionsAsync(int offset, CancellationToken ct)
    {
        if (offset < 0) throw new ArgumentException("Pagination invalide.");
        var sessions = await store.ListSessionsAsync<AiSessionState>(offset, 51, ct);
        return new(sessions.Take(50).Select(session => new AiSessionSummary(
            session.Id, session.Provider, session.Model ?? session.Turns.LastOrDefault()?.Usage?.Model,
            session.PendingRequest is null ? "completed" : session.AttemptDeadline is { } deadline && deadline.AddSeconds(5) > DateTimeOffset.UtcNow ? "running" : "awaitingRetry",
            session.Turns.Count, session.Attempt, session.UpdatedAt, session.AttemptDeadline, session.LastError,
            SumUsage(session.Turns), session.Scope)).ToArray(), offset, sessions.Count > 50);
    }
    private static AiUsage? SumUsage(IReadOnlyList<AiSessionTurn> turns)
    {
        if (turns.Count == 0 || turns.All(t => t.Usage is null)) return null;
        return new(
            turns.All(t => t.Usage?.InputTokens is not null) ? turns.Sum(t => t.Usage!.InputTokens!.Value) : null,
            turns.All(t => t.Usage?.OutputTokens is not null) ? turns.Sum(t => t.Usage!.OutputTokens!.Value) : null,
            turns.All(t => t.Usage?.CachedInputTokens is not null) ? turns.Sum(t => t.Usage!.CachedInputTokens!.Value) : null,
            turns.All(t => t.Usage?.EstimatedCostUsd is not null) ? turns.Sum(t => t.Usage!.EstimatedCostUsd!.Value) : null);
    }
    public async Task<AiCompletionResult> GenerateAsync(string? provider, AiPrompt prompt, CancellationToken ct, bool localOnly = false, bool allowModelChanges = false)
    {
        provider ??= (await GetSettingsAsync(ct)).DefaultProvider;
        if (provider is not ("codex-cli" or "claude-cli" or "ollama" or "openai")) throw new ArgumentException("Fournisseur IA inconnu.");
        if (localOnly && provider != "ollama") return new(false, "", "Le mode local uniquement exige Ollama.");
        if (localOnly && (!Uri.TryCreate(settings.GetSettings().OllamaBaseUrl, UriKind.Absolute, out var localUri) || !localUri.IsLoopback))
            return new(false, "", "Ollama doit être configuré sur une adresse de boucle locale pour cette politique.");
        var requestId = prompt.RequestId ?? Guid.NewGuid();
        var id = prompt.SessionId ?? $"{transport.LocalId:N}.{requestId:N}";
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(prompt.UserPrompt) || prompt.UserPrompt.Length + prompt.SystemPrompt.Length > 120000)
            throw new ArgumentException("Demande IA invalide ou trop volumineuse.");
        if (prompt.Scope is { } scope && (string.IsNullOrWhiteSpace(scope.Operation) || scope.Operation.Length > 80
            || new[] { scope.LibraryId, scope.FlowId, scope.MediaId, scope.ExecutionId }.Any(value => value == Guid.Empty)))
            throw new ArgumentException("Périmètre de session IA invalide.");
        var owner = Owner(id);
        if (owner != transport.LocalId)
        {
            if (localOnly) return new(false, "", "Ce nœud ne coordonne pas cette session.", SessionId: id, RequestId: requestId);
            foreach (var url in await transport.PeersAsync(ct))
            {
                try
                {
                    var node = await transport.CallAsync<AiNodeAvailability>(url, "availability", new { }, TimeSpan.FromSeconds(15), ct);
                    if (node.NodeId == owner) return await transport.CallAsync<AiCompletionResult>(url, "generate", new AiGenerateRequest(provider, prompt with { SessionId = id, RequestId = requestId }, allowModelChanges), TimeSpan.FromMinutes(35), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
            }
            return new(false, "", "Coordinateur de session indisponible. Le contexte validé est conservé ; réessayez après son retour.", SessionId: id, RequestId: requestId);
        }
        var gate = gates.GetOrAdd(id, _ => new(1));
        if (!await gate.WaitAsync(0, ct)) return new(false, "", "Une demande est déjà en cours dans cette session.", SessionId: id, RequestId: requestId);
        try
        {
            var session = await GetSessionAsync(id, ct) ?? new(id, provider, prompt.SystemPrompt, [], Model: prompt.Model, Scope: prompt.Scope, ReasoningEffort: prompt.ReasoningEffort);
            if (session.SystemPrompt != prompt.SystemPrompt || session.Scope != prompt.Scope
                || !allowModelChanges && (session.Provider != provider || session.Model != prompt.Model || session.ReasoningEffort != prompt.ReasoningEffort))
                throw new ArgumentException("La configuration de cette session ne peut pas être modifiée.");
            var selection = new AiTurnModelSelection(provider, prompt.Model, prompt.ReasoningEffort, prompt.NodeId);
            var completed = session.Turns.FirstOrDefault(t => t.RequestId == requestId);
            if (completed is not null && (completed.UserPrompt != prompt.UserPrompt || completed.Selection is { } oldSelection && oldSelection != selection))
                throw new ArgumentException("Identifiant de requête déjà utilisé pour une autre demande ou configuration.");
            if (completed is not null) return new(true, completed.Content, SessionId: id, RequestId: requestId, Usage: completed.Usage, ExecutorNodeId: completed.ExecutorNodeId);
            if (session.PendingRequest is { } pending && pending != requestId)
                return new(false, "", "Reprenez d’abord la demande interrompue avec son identifiant.", SessionId: id, RequestId: pending);
            if (session.PendingPrompt is { } saved && (saved.UserPrompt != prompt.UserPrompt || saved.Model != prompt.Model
                || saved.ReasoningEffort != prompt.ReasoningEffort || saved.NodeId != prompt.NodeId || session.Provider != provider))
                throw new ArgumentException("La demande en attente conserve son message, son modèle et son raisonnement.");
            if ((await ModelPolicyAsync(ct)).DisabledModels.Any(m => m.Provider == provider && m.Model == prompt.Model))
                throw new ArgumentException("Ce modèle est désactivé dans le cluster.");
            // A restarted coordinator must wait out the previous executor's hard deadline before issuing another attempt.
            if (session.AttemptDeadline is { } previousDeadline && previousDeadline.AddSeconds(5) > DateTimeOffset.UtcNow)
                return new(false, "", "La tentative précédente peut encore être active. Réessayez après expiration de son bail.", SessionId: id, RequestId: requestId);
            var context = string.Join("\n\n", session.Turns.Select(t => "Utilisateur :\n" + t.UserPrompt + "\nAssistant :\n" + t.Content));
            var executionPrompt = prompt with { UserPrompt = context + "\n\nUtilisateur :\n" + prompt.UserPrompt, SessionId = null, RequestId = null };
            if (executionPrompt.UserPrompt.Length > 240000) return new(false, "", "Contexte maximal atteint. Ouvrez une nouvelle session.", SessionId: id, RequestId: requestId);
            session = session with { Provider = provider, Model = prompt.Model, ReasoningEffort = prompt.ReasoningEffort, PendingRequest = requestId, PendingPrompt = prompt with { SessionId = id, RequestId = requestId }, UpdatedAt = DateTimeOffset.UtcNow, LastError = null };
            await store.SaveAsync("session/" + id, session, ct);
            var candidates = new List<(string? Url, AiNodeAvailability Node)> { (null, await LocalAsync(ct)) };
            foreach (var url in await transport.PeersAsync(ct))
            {
                try { candidates.Add((url, await transport.CallAsync<AiNodeAvailability>(url, "availability", new { }, TimeSpan.FromSeconds(15), ct))); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
            }
            AiCompletionResult result = new(false, "", "Aucun nœud connecté à ce fournisseur. La demande reste en attente.");
            foreach (var candidate in candidates.DistinctBy(c => c.Node.NodeId)
                .Where(c => Supports(c.Node, provider, prompt)
                    && (!localOnly || c.Url is null))
                .Take(localOnly ? 1 : 3))
            {
                var seconds = Math.Clamp((await GetSettingsAsync(ct)).TimeoutSeconds, 10, 600);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
                session = session with { AttemptDeadline = deadline, Attempt = session.Attempt + 1 };
                await store.SaveAsync("session/" + id, session, ct);
                var attempt = new AiExecuteRequest(Guid.NewGuid(), provider, executionPrompt, deadline);
                var ambiguous = false;
                try
                {
                    result = candidate.Url is null ? await ExecuteAsync(attempt, ct)
                        : await transport.CallAsync<AiCompletionResult>(candidate.Url, "execute", attempt, TimeSpan.FromSeconds(seconds + 5), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                { ambiguous = true; result = new(false, "", "Le nœud d’exécution n’a pas confirmé de résultat."); }
                if (result.Success)
                {
                    session = session with { Turns = [.. session.Turns, new(requestId, prompt.UserPrompt, result.Content, result.Usage, DateTimeOffset.UtcNow, selection, result.ExecutorNodeId)], PendingRequest = null, PendingPrompt = null, AttemptDeadline = null, UpdatedAt = DateTimeOffset.UtcNow, LastError = null };
                    await store.SaveAsync("session/" + id, session, ct);
                    return result with { SessionId = id, RequestId = requestId };
                }
                // Never issue concurrent attempts after an ambiguous transport failure.
                var wait = deadline.AddSeconds(5) - DateTimeOffset.UtcNow;
                if (ambiguous && wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
            await store.SaveAsync("session/" + id, session with { AttemptDeadline = null, UpdatedAt = DateTimeOffset.UtcNow, LastError = result.ErrorMessage }, ct);
            return result with { SessionId = id, RequestId = requestId };
        }
        finally { gate.Release(); }
    }
    private async Task<AiCompletionResult> ExecuteAsync(AiExecuteRequest request, CancellationToken ct)
    {
        if (request.Id == Guid.Empty || request.Deadline <= DateTimeOffset.UtcNow || request.Deadline > DateTimeOffset.UtcNow.AddSeconds(605))
            return new(false, "", "Bail IA expiré ou invalide.");
        // Local durable claim prevents replay, including after restart. A lost result is never silently re-executed under the same attempt ID.
        return await running.GetOrAdd(request.Id, _ => new Lazy<Task<AiCompletionResult>>(() => RunClaimedAsync(request, ct))).Value;
    }
    private async Task<AiCompletionResult> RunClaimedAsync(AiExecuteRequest request, CancellationToken ct)
    {
        var directory = Path.Combine(AiCliRuntime.ResolveRoot(configuration), "attempts");
        Directory.CreateDirectory(directory);
        try { using var claim = new FileStream(Path.Combine(directory, request.Id.ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) { return new(false, "", "Tentative déjà reçue ; reprenez depuis le dernier résultat validé."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(request.Deadline - DateTimeOffset.UtcNow);
        try
        {
            var localSettings = await GetSettingsAsync(deadline.Token);
            if (request.Provider == "codex-cli" && !localSettings.CodexEnabled
                || request.Provider == "claude-cli" && !localSettings.ClaudeEnabled)
                return new(false, "", "Ce fournisseur est désactivé dans le cluster.");
            var local = await LocalAsync(deadline.Token);
            if (!Supports(local, request.Provider, request.Prompt)) return new(false, "", "Modèle ou fournisseur indisponible sur ce nœud.");
            var effectiveModel = request.Prompt.Model ?? local.Models?.GetValueOrDefault(request.Provider)
                ?? local.Catalogs?.FirstOrDefault(c => c.Provider == request.Provider && c.State == "available")?.Models.FirstOrDefault(m => m.IsDefault)?.Id;
            if ((await ModelPolicyAsync(deadline.Token)).DisabledModels.Any(m => m.Provider == request.Provider && m.Model == effectiveModel))
                return new(false, "", "Ce modèle est désactivé dans le cluster.");
            var shared = await GetSettingsAsync(deadline.Token);
            if (request.Provider == "codex-cli" && !shared.CodexEnabled
                || request.Provider == "claude-cli" && !shared.ClaudeEnabled)
                return new(false, "", "Ce fournisseur est désactivé dans le cluster.");
            settings.UpdateSettings(new(DefaultProvider: shared.DefaultProvider, TimeoutSeconds: shared.TimeoutSeconds,
                SubtitleBatchSize: shared.SubtitleBatchSize, CodexEnabled: shared.CodexEnabled, ClaudeEnabled: shared.ClaudeEnabled));
            return (await factory.CreateLocalClient(request.Provider).GenerateTextAsync(request.Prompt, deadline.Token)) with { ExecutorNodeId = transport.LocalId };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "", "Bail d’exécution IA expiré."); }
    }
    public async Task<AiSessionState> ForkSessionAsync(string id, CancellationToken ct)
    {
        var saved = await GetSessionAsync(id, ct) ?? throw new ArgumentException("Checkpoint encore indisponible sur ce nœud. Réessayez après réconciliation.");
        if (saved.AttemptDeadline is { } deadline && deadline.AddSeconds(5) > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Attendez la fin du bail de la tentative précédente avant de créer une nouvelle session.");
        var fork = saved with { Id = $"{transport.LocalId:N}.{Guid.NewGuid():N}", PendingRequest = null, PendingPrompt = null, AttemptDeadline = null, Attempt = 0, UpdatedAt = DateTimeOffset.UtcNow, LastError = null };
        await store.SaveAsync("session/" + fork.Id, fork, ct);
        return fork;
    }
    private static Guid Owner(string id)
    {
        var parts = id.Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var owner) || owner == Guid.Empty || !Guid.TryParseExact(parts[1], "N", out _))
            throw new ArgumentException("Identifiant de session invalide.");
        return owner;
    }
    public async Task<AiSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var shared = await store.ReadAsync<AiSettingsDto>("settings", ct);
        var local = settings.GetSettings();
        return shared is null ? local : local with
        {
            DefaultProvider = shared.DefaultProvider,
            DefaultModel = shared.DefaultModel,
            DefaultReasoningEffort = shared.DefaultReasoningEffort,
            TimeoutSeconds = shared.TimeoutSeconds,
            SubtitleBatchSize = shared.SubtitleBatchSize,
            AssistantProvider = shared.AssistantProvider,
            AssistantModel = shared.AssistantModel,
            AssistantReasoningEffort = shared.AssistantReasoningEffort,
            FlowProvider = shared.FlowProvider,
            FlowModel = shared.FlowModel,
            FlowReasoningEffort = shared.FlowReasoningEffort,
            AssistantLocalOnly = shared.AssistantLocalOnly,
            FlowLocalOnly = shared.FlowLocalOnly,
            AssistantConversationRetentionDays = shared.AssistantConversationRetentionDays,
            AssistantActivityRetentionDays = shared.AssistantActivityRetentionDays,
            OllamaEnabled = shared.OllamaEnabled,
            OpenAiEnabled = shared.OpenAiEnabled,
            CodexEnabled = shared.CodexEnabled,
            ClaudeEnabled = shared.ClaudeEnabled
        };
    }
    public async Task<AiSettingsDto> UpdateSettingsAsync(AiSettingsUpdateRequest update, CancellationToken ct)
    {
        var saved = settings.UpdateSettings(update);
        await store.SaveAsync("settings", saved with { OpenAiApiKey = null }, ct);
        return saved;
    }
}
