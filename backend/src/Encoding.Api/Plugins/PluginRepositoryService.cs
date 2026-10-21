using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Contracts.Plugins;
using Encoding.Core.Plugins;

namespace Encoding.Api.Plugins;

public sealed record RepositoryVersion(string Version, string DownloadUrl, string Sha256, string? Changelog, DateTimeOffset? ReleaseDate);

public sealed record RepositoryPlugin(string Id, string Name, string Summary, string Category, string CategoryLabel, string? Author,
    string LatestVersion, IReadOnlyList<string> RequiredTools, IReadOnlyList<RepositoryVersion> Versions);

public sealed record PluginRepository(string Id, string Name, string Url, string Description, bool IsOfficial, bool Enabled,
    DateTimeOffset? LastSyncAt, string? LastError, IReadOnlyList<RepositoryPlugin> Packages);

public sealed class PluginRepositoryException(string message) : Exception(message);

/// <summary>
/// Dépôts de plugins décentralisés (0063) : simples fichiers <c>repository.json</c> statiques. Le dépôt officiel n'existe que si
/// <c>PluginStore:OfficialRepositoryUrl</c> est configuré ; lui seul peut distribuer l'espace de noms « official ».
/// Chaque archive est contrôlée par le SHA-256 annoncé dans l'index avant toute installation.
/// </summary>
public sealed partial class PluginRepositoryService(IConfiguration configuration, IHttpClientFactory clients, PluginStoreService store, ILogger<PluginRepositoryService> logger)
{
    public const string HttpClientName = "PluginStore";
    private const int MaxIndexBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private string FilePath => Path.Combine(store.Directory, "repositories.json");
    private string? OfficialUrl => configuration["PluginStore:OfficialRepositoryUrl"] is { Length: > 0 } url ? url : null;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")] private static partial Regex Sha256Pattern();
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")] private static partial Regex VersionPattern();

    public static string IdFor(string url) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.Trim().TrimEnd('/')))).ToLowerInvariant()[..12];

    public IReadOnlyList<PluginRepository> List()
    {
        var saved = Read();
        if (OfficialUrl is { } official && saved.All(item => !item.IsOfficial))
            saved.Insert(0, new PluginRepository("official", "Dépôt officiel", official, "", true, true, null, null, []));
        return saved;
    }

    private List<PluginRepository> Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<PluginRepository>>(File.ReadAllText(FilePath), Json) ?? [] : []; }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            logger.LogError(exception, "La liste des dépôts de plugins est illisible : {Path}.", FilePath);
            return [];
        }
    }

    private void Write(List<PluginRepository> repositories)
    {
        Directory.CreateDirectory(store.Directory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(repositories, Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    public static Uri ValidateUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new PluginRepositoryException("L'adresse du dépôt doit être une URL HTTP ou HTTPS sans identifiant intégré.");
        return uri;
    }

    public async Task<PluginRepository> AddAsync(string? url, CancellationToken ct)
    {
        var uri = ValidateUrl(url);
        var id = IdFor(uri.ToString());
        await gate.WaitAsync(ct);
        try
        {
            var all = List().ToList();
            if (all.Any(item => item.Id == id || string.Equals(item.Url.TrimEnd('/'), uri.ToString().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                throw new PluginRepositoryException("Ce dépôt est déjà configuré.");
            var fetched = await FetchAsync(new PluginRepository(id, uri.Host, uri.ToString(), "", false, true, null, null, []), ct);
            all.Add(fetched);
            Write(all.Where(item => !item.IsOfficial || item.LastSyncAt is not null).ToList());
            return fetched;
        }
        finally { gate.Release(); }
    }

    public async Task<PluginRepository?> SyncAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var all = List().ToList();
            var index = all.FindIndex(item => item.Id == id);
            if (index < 0) return null;
            PluginRepository result;
            try { result = await FetchAsync(all[index], ct); }
            catch (PluginRepositoryException exception) { result = all[index] with { LastError = exception.Message, LastSyncAt = DateTimeOffset.UtcNow }; }
            all[index] = result;
            Write(all);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<PluginRepository?> SetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var all = List().ToList();
            var index = all.FindIndex(item => item.Id == id);
            if (index < 0) return null;
            all[index] = all[index] with { Enabled = enabled };
            Write(all);
            return all[index];
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var all = List().ToList();
            var target = all.SingleOrDefault(item => item.Id == id);
            if (target is null) return false;
            if (target.IsOfficial) throw new PluginRepositoryException("Le dépôt officiel est verrouillé : désactivez-le plutôt que de le supprimer.");
            all.Remove(target);
            Write(all);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task<PluginRepository> FetchAsync(PluginRepository repository, CancellationToken ct)
    {
        string body;
        try
        {
            using var response = await clients.CreateClient(HttpClientName).GetAsync(repository.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new PluginRepositoryException($"Le dépôt a répondu avec le statut HTTP {(int)response.StatusCode}.");
            body = System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(response, MaxIndexBytes, "L'index du dépôt dépasse 4 Mo.", ct));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new PluginRepositoryException("Le dépôt est injoignable ou ne répond pas dans le délai imparti.");
        }
        return ParseIndex(repository, body);
    }

    public static PluginRepository ParseIndex(PluginRepository repository, string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new PluginRepositoryException("L'index du dépôt n'est pas un JSON valide."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PluginRepositoryException("L'index du dépôt doit être un objet JSON.");
            var schemaVersion = Text(root, "schemaVersion");
            if (schemaVersion is null || !schemaVersion.StartsWith("1.", StringComparison.Ordinal))
                throw new PluginRepositoryException($"Version de schéma du dépôt non prise en charge : « {schemaVersion} » (1.x attendu).");
            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("categories", out var categoryList) && categoryList.ValueKind == JsonValueKind.Array)
                foreach (var category in categoryList.EnumerateArray())
                    if (Text(category, "id") is { } categoryId) categories[categoryId] = Text(category, "name") ?? categoryId;
            var packages = new List<RepositoryPlugin>();
            var skipped = 0;
            if (root.TryGetProperty("plugins", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var plugin = ParsePlugin(item, categories, repository.IsOfficial);
                    if (plugin is null) skipped++;
                    else if (packages.All(existing => existing.Id != plugin.Id)) packages.Add(plugin);
                    else skipped++;
                }
            }
            return repository with
            {
                Name = Text(root, "name") ?? repository.Name,
                Description = Text(root, "description") ?? "",
                LastSyncAt = DateTimeOffset.UtcNow,
                LastError = skipped > 0 ? $"{skipped} entrée(s) invalide(s) ignorée(s)." : null,
                Packages = packages
            };
        }
    }

    private static RepositoryPlugin? ParsePlugin(JsonElement item, Dictionary<string, string> categories, bool official)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var id = Text(item, "id");
        if (id is null || !PluginIdentity.IsQualifiedPluginId(id) || PluginIdentity.IsOfficialNamespace(id) && !official) return null;
        var versions = new List<RepositoryVersion>();
        if (item.TryGetProperty("versions", out var versionList) && versionList.ValueKind == JsonValueKind.Array)
            foreach (var entry in versionList.EnumerateArray())
            {
                var version = Text(entry, "version");
                var url = Text(entry, "downloadUrl");
                var sha = Text(entry, "sha256");
                if (version is null || !VersionPattern().IsMatch(version) || sha is null || !Sha256Pattern().IsMatch(sha)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
                    || versions.Any(existing => existing.Version == version)) continue;
                DateTimeOffset? released = entry.TryGetProperty("releaseDate", out var date) && date.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(date.GetString(), out var parsed) ? parsed : null;
                versions.Add(new RepositoryVersion(version, uri.ToString(), sha.ToLowerInvariant(), Text(entry, "changelog"), released));
            }
        var latest = Text(item, "latestVersion");
        if (versions.Count == 0 || latest is null || versions.All(version => version.Version != latest)) return null;
        var category = Text(item, "category") ?? "utilities";
        var tools = item.TryGetProperty("requiredTools", out var toolList) && toolList.ValueKind == JsonValueKind.Array
            ? toolList.EnumerateArray().Where(tool => tool.ValueKind == JsonValueKind.String).Select(tool => tool.GetString()!).ToArray() : [];
        return new RepositoryPlugin(id, Text(item, "name") ?? id, Text(item, "summary") ?? "", category,
            categories.GetValueOrDefault(category, category), Text(item, "author"), latest, tools, versions);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : null;

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, string message, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new PluginRepositoryException(message);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > limit) throw new PluginRepositoryException(message);
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>Télécharge, contrôle l'empreinte puis installe une version d'un paquet d'un dépôt.</summary>
    public async Task<InstalledPlugin> InstallAsync(string repositoryId, string pluginId, string? version, CancellationToken ct)
    {
        var repository = List().SingleOrDefault(item => item.Id == repositoryId) ?? throw new PluginRepositoryException("Dépôt inconnu.");
        if (!repository.Enabled) throw new PluginRepositoryException("Ce dépôt est désactivé.");
        var plugin = repository.Packages.SingleOrDefault(item => item.Id == pluginId) ?? throw new PluginRepositoryException("Ce paquet n'existe pas dans le dépôt.");
        var wanted = plugin.Versions.SingleOrDefault(item => item.Version == (version ?? plugin.LatestVersion)) ?? throw new PluginRepositoryException("Cette version n'existe pas dans le dépôt.");
        byte[] archive;
        try
        {
            using var response = await clients.CreateClient(HttpClientName).GetAsync(wanted.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new PluginRepositoryException($"Le téléchargement a échoué (HTTP {(int)response.StatusCode}).");
            archive = await ReadLimitedAsync(response, PluginPackage.MaxArchiveBytes, "L'archive dépasse 32 Mo.", ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new PluginRepositoryException("L'archive est injoignable ou ne se télécharge pas dans le délai imparti.");
        }
        try { return await store.ImportAsync(archive, "repo:" + repository.Id, plugin.Id, wanted.Version, wanted.Sha256, allowOfficial: repository.IsOfficial); }
        catch (PluginPackageException exception) { throw new PluginRepositoryException(exception.Message); }
    }
}
