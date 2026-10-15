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
    private readonly ConcurrentDictionary<string, byte> manualUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient probeClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private MdnsBrowser? browser;

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
            while (!stoppingToken.IsCancellationRequested)
            {
                await AddConfiguredClusterAsync(stoppingToken);
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

    /// <summary>Ajoute un cluster par son adresse quand la découverte réseau ne le voit pas (réseau Docker en pont, autre sous-réseau).</summary>
    public async Task<DiscoveredCluster> AddManualAsync(string address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("L’adresse du cluster doit être une URL http(s) complète, par exemple http://192.168.1.51:8090.");
        var baseUrl = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/").AbsoluteUri;
        var cluster = await ProbeAsync(baseUrl, cancellationToken)
            ?? throw new InvalidOperationException("Aucun cluster Encoding ne répond à cette adresse.");
        manualUrls[baseUrl] = 0;
        return cluster;
    }

    private async Task AddConfiguredClusterAsync(CancellationToken cancellationToken)
    {
        var urls = manualUrls.Keys.AsEnumerable();
        if (options.ClusterApiBaseUrl is not null) urls = urls.Append(options.ClusterApiBaseUrl);
        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (await ProbeAsync(url, cancellationToken) is { } cluster)
                    logger.LogInformation("Cluster configuré découvert : {ClusterName} ({ClusterId}).", cluster.Name, cluster.Id);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                logger.LogDebug("Le point de découverte de cluster configuré n’est pas encore disponible.");
            }
        }
    }

    private async Task<DiscoveredCluster?> ProbeAsync(string apiBaseUrl, CancellationToken cancellationToken)
    {
        var identity = await probeClient.GetFromJsonAsync<ClusterIdentity>(
            new Uri(new Uri(apiBaseUrl), "api/v1/cluster"), cancellationToken);
        if (identity is null || identity.Id == Guid.Empty) return null;
        var cluster = new DiscoveredCluster(identity.Id, identity.Name, apiBaseUrl, DateTimeOffset.UtcNow);
        clusters[identity.Id] = cluster;
        return cluster;
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
        probeClient.Dispose();
        base.Dispose();
    }

    private sealed record ClusterIdentity(Guid Id, string Name);
}
