using System.Collections.Concurrent;
using System.Text.Json;
using Haukcode.Mdns;

namespace Encoding.Api.Infrastructure;

/// <summary>Découvre les autres serveurs du même cluster et échange les événements de réconciliation au mieux.</summary>
public sealed class ClusterReconciliationService(
    ClusterIdentityRepository identityRepository,
    ClusterReconciliationRepository repository,
    ClusterEntityReconciliation entityReconciliation,
    ClusterPeerIdentityStore localIdentity,
    ClusterPeerAdmissionRepository peerAdmissions,
    ClusterDataKeyStore dataKeyStore,
    ClusterReconciliationHealth health,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<ClusterReconciliationService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Guid> discoveredPeers = new(StringComparer.OrdinalIgnoreCase);
    private MdnsBrowser? browser;
    private Guid clusterId;
    private string? advertisedApiBaseUrl;
    private string? ownInstanceName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var identity = await identityRepository.GetAsync(stoppingToken);
            clusterId = identity.Id;
            advertisedApiBaseUrl = Normalize(configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]);
            ownInstanceName = configuration["Cluster:DiscoveryInstanceName"]?.Trim();
            if (string.IsNullOrWhiteSpace(ownInstanceName)) ownInstanceName = Environment.MachineName;
            try
            {
                browser = new MdnsBrowser("_encoding._tcp");
                browser.ServiceFound += OnServiceFound;
                browser.ServiceLost += OnServiceLost;
                browser.Start();
                health.SetDiscovery(true);
                logger.LogInformation("Découverte mDNS des pairs de réconciliation démarrée pour le cluster {ClusterId}.", clusterId);
            }
            catch (Exception exception)
            {
                health.SetDiscovery(false, exception.Message);
                logger.LogWarning(exception, "La découverte mDNS des pairs est indisponible ; les adresses configurées restent utilisables.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var currentIdentity = await identityRepository.GetAsync(stoppingToken);
                if (currentIdentity.Id != clusterId)
                {
                    clusterId = currentIdentity.Id;
                    logger.LogInformation("L’identité locale a rejoint le cluster {ClusterId} ; le maillage reprend avec cette identité.", clusterId);
                }
                var hasDataKey = false;
                string? recoveryError = null;
                try { hasDataKey = dataKeyStore.HasKey(clusterId); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    recoveryError = exception.Message;
                }
                if (!hasDataKey)
                {
                    try { await peerAdmissions.RecoverLocalDataKeyAsync(stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception)
                    {
                        recoveryError = exception.Message;
                        logger.LogDebug(exception, "La clé de données locale n’a pas pu être restaurée depuis l’admission signée.");
                    }
                    try { hasDataKey = dataKeyStore.HasKey(clusterId); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        recoveryError = exception.Message;
                        hasDataKey = false;
                    }
                    if (!hasDataKey)
                    {
                        health.Disable(recoveryError is null
                            ? "La clé de données du cluster sera installée à la fin du rattachement de ce serveur."
                            : $"La clé de données du cluster est indisponible : {recoveryError}");
                        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                        continue;
                    }
                }
                health.Enable();
                try
                {
                    await entityReconciliation.CaptureLocalChangesAsync(stoppingToken);
                    await entityReconciliation.ApplyPendingAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "La capture ou la projection des données du cluster sera retentée.");
                }
                var peerUrls = GetPeerUrls().ToArray();
                var configuredUrls = (configuration.GetSection("Cluster:Reconciliation:Peers").Get<string[]>() ?? [])
                    .Select(Normalize).Where(url => url is not null).Cast<string>();
                var discoveredUrls = discoveredPeers.Where(peer => peer.Value == clusterId).Select(peer => peer.Key);
                health.SetPeers(configuredUrls, discoveredUrls);
                foreach (var peerUrl in peerUrls)
                {
                    health.BeginAttempt(peerUrl);
                    try
                    {
                        await ReconcilePeerAsync(peerUrl, stoppingToken);
                        health.Succeed(peerUrl);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        health.Fail(peerUrl, exception.Message);
                        logger.LogDebug(exception, "Réconciliation différée avec le pair {PeerUrl}.", peerUrl);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Le service de réconciliation du cluster s’est arrêté.");
        }
    }

    private IEnumerable<string> GetPeerUrls()
    {
        var configured = configuration.GetSection("Cluster:Reconciliation:Peers").Get<string[]>() ?? [];
        return configured.Concat(discoveredPeers.Where(peer => peer.Value == clusterId).Select(peer => peer.Key))
            .Select(Normalize)
            .Where(url => url is not null && !string.Equals(url, advertisedApiBaseUrl, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task ReconcilePeerAsync(string peerUrl, CancellationToken ct)
    {
        var remoteVector = await repository.GetPeerVectorAsync(peerUrl, ct);
        var localVector = await repository.GetVectorAsync(ct);
        var outgoing = (await repository.GetEventsAfterAsync(clusterId, remoteVector, ClusterReconciliationRepository.MaximumBatchSize, ct)).ToList();
        byte[] requestBody;
        while (true)
        {
            var request = new ClusterReconciliationExchangeRequest(
                ClusterReconciliationEndpoints.ProtocolVersion,
                clusterId,
                localVector,
                outgoing);
            requestBody = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            if (requestBody.Length <= ClusterReconciliationSignature.MaximumBodyBytes) break;
            if (outgoing.Count == 0) throw new InvalidOperationException("Le vecteur de réconciliation dépasse la taille maximale autorisée.");
            outgoing.RemoveAt(outgoing.Count - 1);
        }
        var requestTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(peerUrl), "api/v1/cluster/reconciliation/exchange"))
        {
            Content = new ByteArrayContent(requestBody)
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        message.Headers.Add(ClusterReconciliationSignature.PeerHeader, localIdentity.PeerId.ToString("D"));
        message.Headers.Add(ClusterReconciliationSignature.TimeHeader, requestTime);
        message.Headers.Add(ClusterReconciliationSignature.SignatureHeader, ClusterReconciliationSignature.Create(localIdentity, requestTime, requestBody));
        using var response = await httpClientFactory.CreateClient("ClusterReconciliation").SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Le pair a refusé l’identité ou la signature de ce serveur. Vérifiez son approbation dans le cluster.");
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                throw new InvalidOperationException("Le pair indique une identité de cluster différente.");
            throw new InvalidOperationException($"Réponse HTTP {(int)response.StatusCode} du pair.");
        }
        var responseBody = await ReadBoundedBodyAsync(response.Content, ct);
        if (responseBody is null)
        {
            throw new InvalidOperationException("Réponse de réconciliation trop volumineuse.");
        }
        response.Headers.TryGetValues(ClusterReconciliationSignature.PeerHeader, out var responsePeers);
        response.Headers.TryGetValues(ClusterReconciliationSignature.TimeHeader, out var responseTimes);
        response.Headers.TryGetValues(ClusterReconciliationSignature.SignatureHeader, out var responseSignatures);
        if (!Guid.TryParse(responsePeers?.FirstOrDefault(), out var responsePeerId)
            || await peerAdmissions.GetActivePublicKeyAsync(clusterId, responsePeerId, ct) is not { } responsePublicKey
            || !ClusterReconciliationSignature.Verify(responsePublicKey, responsePeerId,
                responseTimes?.FirstOrDefault(),
                responseSignatures?.FirstOrDefault(),
                responseBody))
        {
            throw new InvalidOperationException("La signature de la réponse du pair est invalide.");
        }
        var exchange = JsonSerializer.Deserialize<ClusterReconciliationExchangeResponse>(responseBody, JsonOptions);
        if (exchange is null || exchange.ProtocolVersion != ClusterReconciliationEndpoints.ProtocolVersion || exchange.ClusterId != clusterId)
        {
            throw new InvalidOperationException("Réponse de réconciliation incompatible avec ce cluster.");
        }
        await repository.ApplyIncomingAsync(exchange.Changes, ct);
        await entityReconciliation.ApplyPendingAsync(ct);
        await repository.SavePeerVectorAsync(peerUrl, exchange.KnownSequences, ct);
        if (outgoing.Count > 0 || exchange.Changes.Count > 0)
            logger.LogDebug("Réconciliation avec {PeerUrl} : {Sent} événements envoyés, {Received} reçus.", peerUrl, outgoing.Count, exchange.Changes.Count);
    }

    private void OnServiceFound(ServiceProfile service)
    {
        if (!service.Properties.TryGetValue("clusterId", out var id) || !Guid.TryParse(id, out var foundCluster)) return;
        if (string.Equals(service.InstanceName, ownInstanceName, StringComparison.OrdinalIgnoreCase)) return;
        var url = service.Properties.TryGetValue("reconciliationApiUrl", out var configuredUrl)
            ? configuredUrl
            : $"http://{service.Hostname.TrimEnd('.')}:{service.Port}/";
        if (Normalize(url) is { } normalized && !string.Equals(normalized, advertisedApiBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            discoveredPeers[normalized] = foundCluster;
            logger.LogDebug("Pair Encoding découvert : {PeerUrl}, cluster {ClusterId}.", normalized, foundCluster);
        }
    }

    private void OnServiceLost(ServiceProfile service)
    {
        var url = service.Properties.TryGetValue("reconciliationApiUrl", out var configuredUrl)
            ? configuredUrl
            : $"http://{service.Hostname.TrimEnd('.')}:{service.Port}/";
        if (Normalize(url) is { } normalized) discoveredPeers.TryRemove(normalized, out _);
    }

    private static string? Normalize(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/").AbsoluteUri;
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > ClusterReconciliationSignature.MaximumBodyBytes) return null;
        await using var input = await content.ReadAsStreamAsync(ct);
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) return output.ToArray();
            if (output.Length + read > ClusterReconciliationSignature.MaximumBodyBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    public override void Dispose()
    {
        browser?.Dispose();
        browser = null;
        base.Dispose();
    }
}
