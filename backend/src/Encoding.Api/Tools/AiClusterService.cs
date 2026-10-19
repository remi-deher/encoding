using System.Collections.Concurrent;
using System.Text.Json;
using Encoding.Contracts.Tools;
using Encoding.Api.Nodes;

namespace Encoding.Api.Tools;

/// <summary>A stable session coordinator owns writes; inference may move between connected peers without moving secrets.</summary>
public sealed class AiClusterService(AiClusterTransport transport, AiClusterStore store, DeviceCodeAuthService auth,
    AiSettingsService settings, AiClientFactory factory, IConfiguration configuration, LocalNodeRegistry localNode, AiQuotaService? quotaService = null)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AiCompletionResult>>> running = new();
    public async Task<AiNodeAvailability> LocalAsync(CancellationToken ct)
    {
        var providers = (await auth.ListToolsAsync(ct)).Where(t => t.IsInstalled && t.AuthStatus.State == "authorized").Select(t => t.Id).ToList();
        var local = settings.GetSettings();
        var models = new Dictionary<string, string?>(StringComparer.Ordinal);
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
        var displayName = await localNode.ResolveDisplayNameAsync(ct);
        return new(transport.LocalId, displayName ?? configuration["Node:Name"] ?? Environment.MachineName, providers, Models: models);
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
        "generate" => await GenerateAsync(value.Deserialize<AiGenerateRequest>()!.Provider, value.Deserialize<AiGenerateRequest>()!.Prompt, ct),
        "execute" => await ExecuteAsync(value.Deserialize<AiExecuteRequest>()!, ct),
        _ => throw new ArgumentException("Opération IA inconnue.")
    };
    public async Task<AiSessionState?> GetSessionAsync(string id, CancellationToken ct)
    {
        _ = Owner(id);
        return await store.ReadAsync<AiSessionState>("session/" + id, ct);
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
    public async Task<AiCompletionResult> GenerateAsync(string? provider, AiPrompt prompt, CancellationToken ct, bool localOnly = false)
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
                    if (node.NodeId == owner) return await transport.CallAsync<AiCompletionResult>(url, "generate", new AiGenerateRequest(provider, prompt with { SessionId = id, RequestId = requestId }), TimeSpan.FromMinutes(35), ct);
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
            if (session.Provider != provider || session.SystemPrompt != prompt.SystemPrompt || session.Model != prompt.Model || session.Scope != prompt.Scope || session.ReasoningEffort != prompt.ReasoningEffort) throw new ArgumentException("Le moteur, le modèle, l’effort, les consignes et le périmètre sont fixes pour une session. Ouvrez une nouvelle session.");
            var completed = session.Turns.FirstOrDefault(t => t.RequestId == requestId);
            if (completed is not null && completed.UserPrompt != prompt.UserPrompt) throw new ArgumentException("Identifiant de requête déjà utilisé pour une autre demande.");
            if (completed is not null) return new(true, completed.Content, SessionId: id, RequestId: requestId, Usage: completed.Usage);
            if (session.PendingRequest is { } pending && pending != requestId)
                return new(false, "", "Reprenez d’abord la demande interrompue avec son identifiant.", SessionId: id, RequestId: pending);
            if (session.PendingPrompt is { } saved && saved.UserPrompt != prompt.UserPrompt) throw new ArgumentException("L’identifiant de requête est déjà associé à un autre prompt.");
            // A restarted coordinator must wait out the previous executor's hard deadline before issuing another attempt.
            if (session.AttemptDeadline is { } previousDeadline && previousDeadline.AddSeconds(5) > DateTimeOffset.UtcNow)
                return new(false, "", "La tentative précédente peut encore être active. Réessayez après expiration de son bail.", SessionId: id, RequestId: requestId);
            var context = string.Join("\n\n", session.Turns.Select(t => "Utilisateur :\n" + t.UserPrompt + "\nAssistant :\n" + t.Content));
            var executionPrompt = prompt with { UserPrompt = context + "\n\nUtilisateur :\n" + prompt.UserPrompt, SessionId = null, RequestId = null };
            if (executionPrompt.UserPrompt.Length > 240000) return new(false, "", "Contexte maximal atteint. Ouvrez une nouvelle session.", SessionId: id, RequestId: requestId);
            session = session with { PendingRequest = requestId, PendingPrompt = prompt with { SessionId = id, RequestId = requestId }, UpdatedAt = DateTimeOffset.UtcNow, LastError = null };
            await store.SaveAsync("session/" + id, session, ct);
            var candidates = new List<(string? Url, AiNodeAvailability Node)> { (null, await LocalAsync(ct)) };
            foreach (var url in await transport.PeersAsync(ct))
            {
                try { candidates.Add((url, await transport.CallAsync<AiNodeAvailability>(url, "availability", new { }, TimeSpan.FromSeconds(15), ct))); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
            }
            AiCompletionResult result = new(false, "", "Aucun nœud connecté à ce fournisseur. La demande reste en attente.");
            foreach (var candidate in candidates.DistinctBy(c => c.Node.NodeId)
                .Where(c => c.Node.Providers.Contains(provider)
                    && (prompt.Model is null || c.Node.Models?.GetValueOrDefault(provider) == prompt.Model)
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
                    session = session with { Turns = [.. session.Turns, new(requestId, prompt.UserPrompt, result.Content, result.Usage, DateTimeOffset.UtcNow)], PendingRequest = null, PendingPrompt = null, AttemptDeadline = null, UpdatedAt = DateTimeOffset.UtcNow, LastError = null };
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
            if (!(await LocalAsync(deadline.Token)).Providers.Contains(request.Provider)) return new(false, "", "Fournisseur non connecté sur ce nœud.");
            var shared = await GetSettingsAsync(deadline.Token);
            settings.UpdateSettings(new(DefaultProvider: shared.DefaultProvider, TimeoutSeconds: shared.TimeoutSeconds, SubtitleBatchSize: shared.SubtitleBatchSize));
            return await factory.CreateLocalClient(request.Provider).GenerateTextAsync(request.Prompt, deadline.Token);
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
            TimeoutSeconds = shared.TimeoutSeconds,
            SubtitleBatchSize = shared.SubtitleBatchSize,
            AssistantProvider = shared.AssistantProvider,
            FlowProvider = shared.FlowProvider,
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
