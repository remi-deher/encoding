using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Encoding.Api.Nodes;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed class PeerPresenceGossipService(
    PeerClusterOptions options,
    PeerIdentityStore identity,
    PeerTrustStore trustStore,
    PeerPresenceRegistry registry,
    NodeRepository nodes,
    NodePresenceOptions nodePresence,
    ILogger<PeerPresenceGossipService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private long sequence;
    private long incarnation;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        incarnation = identity.StartNewIncarnation();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishAndGossipAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Échec d’un cycle de présence entre pairs.");
            }
            await Task.Delay(options.GossipInterval, stoppingToken);
        }
    }

    private async Task PublishAndGossipAsync(CancellationToken cancellationToken)
    {
        var nodeSnapshot = await nodes.ListAsync(nodePresence.OfflineAfter, cancellationToken);
        var local = new PeerPresenceUnsigned(identity.Identity.ClusterId, identity.Identity.PeerId, incarnation,
            Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, nodeSnapshot);
        registry.Merge(PeerPresenceRegistry.Sign(local, identity), identity.Identity.ClusterId,
            new TrustedPeer(identity.Identity.PeerId, identity.Identity.ClusterId, options.PublicEndpoint!, identity.Identity.Thumbprint, identity.Identity.CertificatePem));
        var batch = new PeerPresenceBatch(registry.Snapshot());
        var json = JsonSerializer.Serialize(batch, JsonOptions);

        foreach (var peer in trustStore.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var handler = CreateHandler(peer);
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(peer.Endpoint, "/api/v1/peer-cluster/presence"))
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    logger.LogDebug("Le pair {PeerId} a refusé le gossip avec HTTP {StatusCode}.", peer.PeerId, (int)response.StatusCode);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                logger.LogDebug("Le pair {PeerId} n’est pas joignable pour le gossip.", peer.PeerId);
            }
        }
    }

    private HttpClientHandler CreateHandler(TrustedPeer peer)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        handler.ClientCertificates.Add(identity.Identity.Certificate);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return PeerIdentity.Fingerprint(presented) == peer.Thumbprint
                && presented.NotBefore.ToUniversalTime() <= DateTimeOffset.UtcNow
                && presented.NotAfter.ToUniversalTime() > DateTimeOffset.UtcNow
                && PeerIdentityStore.CertificateSubjectMatches(presented, peer.ClusterId, peer.PeerId);
        };
        return handler;
    }
}
