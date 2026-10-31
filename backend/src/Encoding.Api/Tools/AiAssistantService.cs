using System.Security.Claims;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Api.Mcp;
using Encoding.Api.Libraries;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Libraries;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>Owns private assistant conversations, resolves context and prepares bounded confirmed actions.</summary>
public sealed partial class AiAssistantService(
    AiClusterStore store,
    AiClusterService cluster,
    AiClusterTransport transport,
    FlowRepository flows,
    ExecutionRepository executions,
    LibraryWorkflowRepository libraryWorkflows,
    LibraryRepository libraries,
    NodeRepository nodes,
    NodePresenceOptions nodePresence,
    McpService mcp,
    IDataProtectionProvider protection,
    FlowTemplateRepository? flowTemplates = null)
{
    private FlowTemplateRepository FlowTemplates => flowTemplates
        ?? throw new InvalidOperationException("Le catalogue de modèles de flow n’est pas disponible.");
    public async Task<AiAssistantOptions> OptionsAsync(CancellationToken ct, bool refresh = false)
    {
        var settings = await cluster.GetSettingsAsync(ct);
        if (refresh) await cluster.ModelsAsync(true, ct);
        var view = await cluster.CachedViewAsync(ct);
        var policy = await cluster.ModelPolicyAsync(ct);
        var nodes = settings.AssistantLocalOnly ? view.Nodes.Take(1) : view.Nodes;
        var catalogs = nodes.Where(n => n.Reachable).Select(n => new AiNodeModelCatalog(n.NodeId, n.Name, n.Catalogs ?? [])).ToArray();
        var options = catalogs.SelectMany(node => node.Providers
            .Where(c => c.State == "available"
                && (!settings.AssistantLocalOnly || c.Provider == "ollama")
                && (c.Provider != "codex-cli" || settings.CodexEnabled)
                && (c.Provider != "claude-cli" || settings.ClaudeEnabled)
                && (c.Provider != "ollama" || settings.OllamaEnabled)
                && (c.Provider != "openai" || settings.OpenAiEnabled)
                && (c.Provider != "anthropic" || settings.AnthropicEnabled))
            .SelectMany(c => c.Models.Where(m => !policy.DisabledModels.Any(d => d.Provider == c.Provider && d.Model == m.Id))
                .Select(m => new AiAssistantModelOption(c.Provider, m.Id, m.Label, m.ReasoningEfforts,
                    node.NodeId, node.Name, m.DefaultReasoningEffort, m.ReasoningKnown, m.IsDefault)))).ToArray();
        var defaultProvider = settings.AssistantProvider ?? settings.DefaultProvider;
        var inheritedProvider = settings.AssistantProvider is null;
        var configuredModel = inheritedProvider ? settings.AssistantModel ?? settings.DefaultModel : settings.AssistantModel;
        var defaultModel = options.FirstOrDefault(option => option.Provider == defaultProvider && option.Model == configuredModel)?.Model
            ?? options.FirstOrDefault(option => option.Provider == defaultProvider && option.IsDefault)?.Model
            ?? options.FirstOrDefault(option => option.Provider == defaultProvider)?.Model;
        var selectedModels = options.Where(option => option.Provider == defaultProvider && option.Model == defaultModel).ToArray();
        var configuredEffort = settings.AssistantReasoningEffort
            ?? (inheritedProvider && (settings.AssistantModel is null || settings.AssistantModel == settings.DefaultModel)
                ? settings.DefaultReasoningEffort : null);
        var defaultEffort = configuredEffort is { } effort
            && selectedModels.Any(option => option.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
                ? effort
                : selectedModels.Select(option => option.DefaultReasoningEffort).FirstOrDefault(value => value is not null);
        return new(defaultProvider, defaultModel, options, catalogs, defaultEffort);
    }

    private static IReadOnlyList<string> ReasoningEfforts(string provider, string? model) => provider switch
    {
        "codex-cli" => ["low", "medium", "high", "xhigh"],
        "claude-cli" => ["low", "medium", "high"],
        "openai" when model is not null && (model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
            => ["low", "medium", "high"],
        _ => [],
    };

    private readonly ConcurrentDictionary<(string OwnerId, Guid RequestId), CancellationTokenSource> activeRequests = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> conversationGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> actionGates = new();
    private readonly IDataProtector actionProtector = protection.CreateProtector("Encoding.AiAssistantAction.v1");
    private const string ConversationPrefix = "assistant/conversations/";
    private const string SessionOwnerPrefix = "assistant/session-owner/";
    private static readonly JsonSerializerOptions ContextJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private sealed record StoredConversation(
        string OwnerId,
        AiAssistantConversation Conversation,
        IReadOnlyDictionary<Guid, IReadOnlyList<AiAssistantSource>>? TurnSources = null,
        IReadOnlyDictionary<Guid, string>? UserPrompts = null,
        IReadOnlyDictionary<Guid, string>? TurnPromptSnapshots = null,
        IReadOnlyDictionary<Guid, string>? TurnToolCalls = null,
        IReadOnlyDictionary<Guid, string>? TurnModelPlans = null,
        IReadOnlyDictionary<Guid, AiTurnModelSelection>? TurnSelections = null,
        IReadOnlyDictionary<Guid, string?>? TurnActualModels = null);
    private sealed record ResolvedContext(AiAssistantSource Source, string Data);
    private const int MaxAssistantFileBytes = 8 * 1024 * 1024;
    private const int MaxAssistantAttachmentBytes = 16 * 1024 * 1024;
    private const int MaxAssistantAttachmentCharacters = 1_000_000;
    private static readonly HashSet<string> AssistantTextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".csv", ".tsv", ".log", ".out",
        ".json", ".jsonc", ".json5", ".yaml", ".yml", ".xml", ".toml", ".ini", ".conf", ".config", ".properties", ".env",
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi", ".sami", ".ttml", ".dfxp", ".mpl", ".lrc", ".sbv",
        ".ffmetadata", ".ffpreset", ".ffscript", ".ffconcat", ".filter", ".m3u", ".m3u8", ".nfo", ".prx", ".avs", ".avsi", ".vpy", ".qpfile", ".chapters", ".edl", ".cue", ".xspf", ".pls", ".mpd",
        ".cs", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".pyw", ".sh", ".bash", ".zsh",
        ".ps1", ".psm1", ".bat", ".cmd", ".sql", ".html", ".htm", ".css", ".scss", ".less", ".proto", ".graphql", ".gql",
        ".dockerfile", ".makefile", ".csproj", ".sln", ".props", ".targets", ".editorconfig", ".gitignore"
    };

    /// <summary>Expire les conversations terminées et l'activité, avec des tombstones répliqués au lieu d'effacer une seule projection locale.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var settings = await cluster.GetSettingsAsync(ct);
        var conversationDays = Math.Clamp(settings.AssistantConversationRetentionDays, 1, 3650);
        var activityDays = Math.Clamp(settings.AssistantActivityRetentionDays, 1, 3650);
        var now = DateTimeOffset.UtcNow;
        var removed = 0;

        var conversations = await store.ReadPrefixAsync<StoredConversation>(ConversationPrefix, ct);
        foreach (var stored in conversations.Where(record => record.Conversation.UpdatedAt < now.AddDays(-conversationDays)))
        {
            var session = await cluster.GetSessionAsync(stored.Conversation.Id, ct);
            if (session?.PendingRequest is not null) continue;

            await store.DeleteAsync(ConversationKey(stored.OwnerId, stored.Conversation.Id), ct);
            await store.DeleteAsync(SessionOwnerKey(stored.Conversation.Id), ct);
            await store.DeleteAsync("session/" + stored.Conversation.Id, ct);
        await store.DeleteAsync(AiClusterService.CoordinatorKey(stored.Conversation.Id), ct);
            foreach (var requestId in stored.TurnPromptSnapshots?.Keys ?? Array.Empty<Guid>())
            {
                var plannerRequestId = DeriveRequestId(
                    stored.Conversation.Id + "/" + requestId.ToString("N"), "assistant-read-tool-planner");
                await DeleteHelperSessionAsync($"{stored.Conversation.Id.Split('.')[0]}.{plannerRequestId:N}", ct);
            }
            removed++;
        }

        var activities = await store.ReadPrefixAsync<StoredActivity>(ActivityPrefix, ct);
        foreach (var stored in activities.Where(record => record.Activity.UpdatedAt < now.AddDays(-activityDays)))
        {
            await store.DeleteAsync(ActivityPrefix + stored.OwnerId + "/" + stored.Activity.RequestId.ToString("N"), ct);
            removed++;
        }
        return removed;
    }
}

public sealed class AiAssistantRetentionService(
    AiAssistantService assistant,
    ClusterReconciliationRepository journal,
    IConfiguration configuration,
    ILogger<AiAssistantRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await assistant.PurgeExpiredAsync(stoppingToken);
                var ownUrl = ClusterReconciliationRepository.TryNormalizePeerUrl(configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]);
                var configuredPeers = configuration.GetSection("Cluster:Reconciliation:Peers").Get<string[]>() ?? [];
                var peers = configuredPeers.Concat(await journal.GetPeerEndpointsAsync(stoppingToken))
                    .Select(ClusterReconciliationRepository.TryNormalizePeerUrl)
                    .OfType<string>()
                    .Where(url => !string.Equals(url, ownUrl, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var purgedEvents = await journal.PruneAssistantHistoryAsync(peers, DateTimeOffset.UtcNow.AddHours(-24), stoppingToken);
                if (removed > 0 || purgedEvents > 0)
                    logger.LogInformation("La rétention IA a expiré {Count} enregistrements et purgé {Events} anciens événements confirmés par les pairs.", removed, purgedEvents);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "La rétention IA a échoué et sera retentée à la prochaine passe.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
