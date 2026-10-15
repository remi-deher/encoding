using System.Collections.Concurrent;
using System.Net.Http.Json;
using Haukcode.Mdns;

namespace Encoding.NodeAgent.Services;

public sealed record DiscoveredCluster(Guid Id, string Name, string ApiBaseUrl, DateTimeOffset DiscoveredAt);

public sealed class WorkerClusterDiscovery(
    NodeAgentOptions options,
    ILogger<WorkerClusterDiscovery> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DiscoveredCluster> clusters = new();
    private MdnsBrowser? browser;

    /// <summary>
    /// Ajoute un cluster par son adresse quand la découverte DNS-SD ne traverse pas le réseau (conteneurs, sous-réseaux distincts).
    /// L'adresse doit répondre sur <c>api/v1/cluster</c> avec l'identité d'un cluster Encoding.
    /// </summary>
    public async Task<DiscoveredCluster> AddByAddressAsync(string address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("L'adresse doit être une URL HTTP ou HTTPS absolue, par exemple http://192.168.1.51:8090.");
        var baseUrl = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        ClusterIdentity? identity;
        try { identity = await client.GetFromJsonAsync<ClusterIdentity>(new Uri(baseUrl, "api/v1/cluster"), cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { throw new HttpRequestException($"Aucun cluster Encoding ne répond à {baseUrl} : {exception.Message}", exception); }
        if (identity is null || identity.Id == Guid.Empty)
            throw new HttpRequestException($"{baseUrl} ne répond pas comme un cluster Encoding.");
        var cluster = new DiscoveredCluster(identity.Id, identity.Name, baseUrl.AbsoluteUri, DateTimeOffset.UtcNow);
        clusters[identity.Id] = cluster;
        logger.LogInformation("Cluster ajouté par adresse : {ClusterName} ({ClusterId}) à {ApiBaseUrl}.", cluster.Name, cluster.Id, cluster.ApiBaseUrl);
        return cluster;
    }

    public IReadOnlyList<DiscoveredCluster> List() => clusters.Values.OrderBy(cluster => cluster.Name).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                browser = new MdnsBrowser("_encoding._tcp");
                browser.ServiceFound += OnServiceFound;
                browser.ServiceLost += OnServiceLost;
                browser.Start();
                logger.LogInformation("Découverte des clusters Encoding démarrée via DNS-SD.");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "La découverte DNS-SD est indisponible sur cette interface réseau.");
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            while (!stoppingToken.IsCancellationRequested)
            {
                await AddConfiguredClusterAsync(client, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "La découverte DNS-SD est indisponible sur cette interface réseau.");
        }
    }

    private async Task AddConfiguredClusterAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (options.ClusterApiBaseUrl is null) return;
        try
        {
            var identity = await client.GetFromJsonAsync<ClusterIdentity>(
                new Uri(new Uri(options.ClusterApiBaseUrl), "api/v1/cluster"), cancellationToken);
            if (identity is null || identity.Id == Guid.Empty) return;
            clusters[identity.Id] = new DiscoveredCluster(identity.Id, identity.Name, options.ClusterApiBaseUrl, DateTimeOffset.UtcNow);
            logger.LogInformation("Cluster configuré découvert : {ClusterName} ({ClusterId}).", identity.Name, identity.Id);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug("Le point de découverte de cluster configuré n’est pas encore disponible.");
        }
    }

    private void OnServiceFound(ServiceProfile service)
    {
        if (!service.Properties.TryGetValue("clusterId", out var idValue)
            || !Guid.TryParse(idValue, out var clusterId)) return;

        var apiBaseUrl = service.Properties.TryGetValue("apiUrl", out var configuredUrl)
            ? configuredUrl
            : $"http://{service.Hostname.TrimEnd('.') }:{service.Port}/";
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;

        var cluster = new DiscoveredCluster(clusterId, service.InstanceName, uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + '/', DateTimeOffset.UtcNow);
        clusters[clusterId] = cluster;
        logger.LogInformation("Cluster découvert : {ClusterName} ({ClusterId}).", cluster.Name, cluster.Id);
    }

    private void OnServiceLost(ServiceProfile service)
    {
        if (service.Properties.TryGetValue("clusterId", out var idValue) && Guid.TryParse(idValue, out var clusterId))
            clusters.TryRemove(clusterId, out _);
    }

    public override void Dispose()
    {
        browser?.Dispose();
        browser = null;
        base.Dispose();
    }

    private sealed record ClusterIdentity(Guid Id, string Name);
}
