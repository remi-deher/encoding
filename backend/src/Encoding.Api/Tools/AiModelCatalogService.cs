using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>Bounded, connection-local catalogue. Credentials and raw provider errors never leave the node.</summary>
public sealed class AiModelCatalogService(AiCliRuntime runtime, AiSettingsService settings, IHttpClientFactory clients,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1);
        public AiModelCatalog? Snapshot;
        public string? Fingerprint;
    }
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    public void Invalidate(string provider) => entries.TryRemove(provider, out _);

    public async Task<AiModelCatalog> ReadAsync(string provider, string connectionRevision, bool refresh, CancellationToken ct)
    {
        var entry = entries.GetOrAdd(provider, _ => new());
        await entry.Gate.WaitAsync(ct);
        try
        {
            var local = settings.GetSettings();
            // Local comparison only; the key is never returned or logged.
            var fingerprint = connectionRevision + (provider == "openai" ? local.OpenAiBaseUrl + local.OpenAiApiKey + local.OpenAiModel
                : provider == "ollama" ? local.OllamaBaseUrl + local.OllamaModel : runtime.Find(provider));
            if (entry.Fingerprint != fingerprint) { entry.Snapshot = null; entry.Fingerprint = fingerprint; }
            var previous = entry.Snapshot;
            if (!refresh && previous is not null && previous.CheckedAt.AddMinutes(1) > clock.GetUtcNow()) return previous;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                IReadOnlyList<AiDiscoveredModel> models;
                if (provider is "codex-cli" or "claude-cli")
                {
                    string[] args = provider == "codex-cli" ? ["app-server"]
                        : ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--no-session-persistence",
                            "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--disable-slash-commands"];
                    models = await runtime.RunProtocolAsync<IReadOnlyList<AiDiscoveredModel>>(provider, args,
                        provider == "codex-cli" ? AiModelProtocol.ReadCodexAsync : AiModelProtocol.ReadClaudeAsync, timeout.Token);
                }
                else
                {
                    var url = provider == "ollama" ? local.OllamaBaseUrl : local.OpenAiBaseUrl;
                    if (provider is not ("ollama" or "openai") || string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException();
                    if (provider == "ollama" && !new Uri(url).AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal)) url = url.TrimEnd('/') + "/v1/";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/models");
                    if (provider == "openai" && !string.IsNullOrWhiteSpace(local.OpenAiApiKey))
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", local.OpenAiApiKey);
                    using var response = await clients.CreateClient("AiClient").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    // Reuse the bounded peer reader so a provider cannot allocate an unbounded response.
                    using var doc = JsonDocument.Parse(await AiClusterTransport.ReadAsync(stream, timeout.Token));
                    var defaultModel = provider == "ollama" ? local.OllamaModel : local.OpenAiModel;
                    models = AiModelProtocol.ParseHttp(doc.RootElement).Select(m => m with { IsDefault = m.Id == defaultModel }).ToArray();
                }
                var now = clock.GetUtcNow();
                return Publish(provider, entry, new(provider, models.Count > 0 ? "available" : "unavailable", models, now, now,
                    models.Count == 0 ? "Le fournisseur ne publie aucun modèle utilisable." : null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or JsonException or IOException
                or ArgumentException or HttpRequestException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
            {
                var now = clock.GetUtcNow();
                return Publish(provider, entry, previous is { Models.Count: > 0 }
                    ? previous with { State = "stale", CheckedAt = now, Message = "Actualisation impossible. Ce catalogue ancien ne permet pas de nouvelle sélection." }
                    : new(provider, "unavailable", [], null, now, "Catalogue indisponible. Vérifiez la connexion et la compatibilité du fournisseur."));
            }
        }
        finally { entry.Gate.Release(); }
    }
    private AiModelCatalog Publish(string provider, Entry entry, AiModelCatalog snapshot)
    {
        if (!entries.TryGetValue(provider, out var current) || !ReferenceEquals(current, entry))
            return new(provider, "unavailable", [], null, clock.GetUtcNow(), "Connexion modifiée. Actualisez le catalogue.");
        entry.Snapshot = snapshot;
        return snapshot;
    }

}
