using Haukcode.Mdns;

namespace Encoding.Api.Infrastructure;

public sealed class ClusterDiscoveryPublisher(
    ClusterIdentityRepository identityRepository,
    IConfiguration configuration,
    ILogger<ClusterDiscoveryPublisher> logger) : IHostedService, IDisposable
{
    private MdnsAdvertiser? advertiser;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var identity = await identityRepository.GetAsync(cancellationToken);
            var configuredPort = configuration.GetValue("Cluster:DiscoveryPort", 8080);
            if (configuredPort is < 1 or > 65535) throw new InvalidOperationException("Cluster:DiscoveryPort doit être compris entre 1 et 65535.");
            var apiUrl = configuration["Cluster:AdvertisedApiBaseUrl"];
            var properties = new Dictionary<string, string> { ["clusterId"] = identity.Id.ToString("D") };
            if (!string.IsNullOrWhiteSpace(apiUrl)) properties["apiUrl"] = apiUrl;
            var profile = new ServiceProfile(identity.Name, "_encoding._tcp", (ushort)configuredPort, properties);
            advertiser = new MdnsAdvertiser(profile);
            advertiser.Start();
            logger.LogInformation("Le cluster {ClusterId} est annoncé sur le réseau local via DNS-SD.", identity.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "La découverte DNS-SD est indisponible ; les workers peuvent utiliser l’adresse directe du serveur.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        advertiser?.Dispose();
        advertiser = null;
    }
}
