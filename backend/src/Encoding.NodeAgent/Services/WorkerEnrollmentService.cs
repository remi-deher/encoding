using System.Net.Http.Json;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;
using System.Runtime.InteropServices;

namespace Encoding.NodeAgent.Services;

public sealed class WorkerEnrollmentService(
    NodeAgentOptions options,
    WorkerIdentityStore identityStore,
    WorkerClusterDiscovery discovery,
    IToolCapabilityProbe capabilityProbe,
    ILogger<WorkerEnrollmentService> logger,
    Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
    LauncherControlClient? launcher = null)
{
    public async Task<NodeResponse> EnrollAsync(Guid clusterId, CancellationToken cancellationToken, bool leaveCurrent = false)
    {
        var cluster = discovery.List().FirstOrDefault(candidate => candidate.Id == clusterId)
            ?? throw new InvalidOperationException("Ce cluster n’est plus visible sur le réseau. Actualisez la découverte et réessayez.");
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled && leaveCurrent && identity.ClusterId != cluster.Id.ToString("D"))
        {
            await ForgetClusterAsync(cancellationToken);
            identity = await identityStore.GetAsync(cancellationToken);
        }
        if (identity.IsEnrolled)
            throw new InvalidOperationException("Ce worker est déjà rattaché à un cluster. Quittez-le d’abord, ou demandez à changer de cluster.");

        var pending = await identityStore.SaveEnrollmentAsync(cluster.Id.ToString("D"), cluster.Name, cluster.ApiBaseUrl, cancellationToken);
        var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
        var request = new NodeEnrollmentRequest(
            pending.Id,
            options.Name,
            options.Roles,
            options.MaxConcurrentTasks,
            capabilities,
            pending.NodeToken!,
            CreateInventory());

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(cluster.ApiBaseUrl), "api/v1/nodes/enroll"))
        {
            Content = JsonContent.Create(request)
        };
        // Le secret du conteneur n'est présenté qu'au serveur local : jamais à un cluster distant.
        if (IsLocalCluster(cluster) && LocalEnrollment.Read(options.LocalEnrollmentSecretFile) is { } secret)
            message.Headers.Add(LocalEnrollment.HeaderName, secret);
        using var response = await client.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("L’enrôlement auprès du cluster {ClusterId} a échoué avec HTTP {StatusCode}.", clusterId, (int)response.StatusCode);
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "L’enrôlement direct est fermé sur ce cluster. Demandez à un administrateur de l’ouvrir depuis la page Nœuds, ou générez une clé de jumelage."
                : $"Le serveur du cluster a refusé l’enrôlement (HTTP {(int)response.StatusCode}).");
        }

        var node = await response.Content.ReadFromJsonAsync<NodeResponse>(jsonOptions.Value.SerializerOptions, cancellationToken)
            ?? throw new HttpRequestException("Le serveur n’a pas retourné la confirmation de l’enrôlement.");
        await identityStore.MarkEnrolledAsync(cancellationToken);
        logger.LogInformation("Le worker {NodeId} a rejoint le cluster {ClusterId}.", node.Id, cluster.Id);
        await RetireLocalServerAsync(cluster.ApiBaseUrl, cancellationToken);
        return node;
    }

    /// <summary>
    /// Quitte le cluster actuel et revient à l'état autonome (0039) : le nœud retrouve son serveur local (donc son cluster à un nœud)
    /// quand le lanceur le gère. Un départ refusé parce que le nœud traite encore un média échoue sans rien changer.
    /// </summary>
    public async Task LeaveAsync(CancellationToken cancellationToken)
    {
        await ForgetClusterAsync(cancellationToken);
        if (launcher is null) return;
        var current = await launcher.GetAsync(cancellationToken);
        if (current is { Server: false })
            await launcher.SetAsync(new NodeMode(true, current.Worker || options.Roles.Contains(NodeRole.Worker)), cancellationToken);
    }

    /// <summary>Prévient le cluster actuel puis efface l'identité locale. Un cluster injoignable n'empêche pas de partir : l'administrateur retirera la ligne hors ligne.</summary>
    private async Task ForgetClusterAsync(CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.HasCredentials)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(identity.ApiBaseUrl!), $"api/v1/nodes/{identity.Id:D}/leave"));
                message.Headers.Add("X-Node-Token", identity.NodeToken);
                using var response = await client.SendAsync(message, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    throw new InvalidOperationException("Ce nœud traite encore un média ou une découverte. Attendez la fin du traitement avant de quitter le cluster.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Le cluster {ClusterName} est injoignable ; le nœud le quitte localement.", identity.ClusterName);
            }
        }
        await identityStore.ResetAsync(cancellationToken);
    }

    /// <summary>Un nœud serveur+worker qui rejoint un autre cluster devient worker seul : son serveur local s'arrête, sa base et ses bibliothèques restent intactes (0039).</summary>
    public async Task RetireLocalServerAsync(string joinedApiBaseUrl, CancellationToken cancellationToken)
    {
        if (launcher is null || IsLocalUrl(joinedApiBaseUrl)) return;
        var current = await launcher.GetAsync(cancellationToken);
        if (current is not { Server: true }) return;
        await launcher.SetAsync(new NodeMode(false, true), cancellationToken);
        logger.LogInformation("Le serveur local est arrêté : ce nœud est désormais un worker du cluster rejoint.");
    }

    /// <summary>Vrai pour le serveur du même conteneur : l'agent est configuré avec un secret local et le cluster est joignable en boucle locale.</summary>
    public bool IsLocalCluster(DiscoveredCluster cluster) => IsLocalUrl(cluster.ApiBaseUrl);

    public bool IsLocalUrl(string apiBaseUrl) =>
        options.LocalEnrollmentSecretFile is not null
        && options.ClusterApiBaseUrl is not null
        && string.Equals(apiBaseUrl, options.ClusterApiBaseUrl, StringComparison.OrdinalIgnoreCase)
        && LocalEnrollment.IsLoopback(apiBaseUrl);

    public async Task<WorkerPairingResponse> CreatePairingKeyAsync(Guid clusterId, CancellationToken cancellationToken, bool leaveCurrent = false)
    {
        var cluster = discovery.List().FirstOrDefault(candidate => candidate.Id == clusterId)
            ?? throw new InvalidOperationException("Ce cluster n’est plus visible sur le réseau. Actualisez la découverte et réessayez.");
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled && leaveCurrent && identity.ClusterId != cluster.Id.ToString("D"))
        {
            await ForgetClusterAsync(cancellationToken);
            identity = await identityStore.GetAsync(cancellationToken);
        }
        if (identity.IsEnrolled)
            throw new InvalidOperationException("Ce worker est déjà rattaché à un cluster. Quittez-le d’abord, ou demandez à changer de cluster.");

        var pending = await identityStore.SavePairingAsync(cluster.Id.ToString("D"), cluster.Name, cluster.ApiBaseUrl, cancellationToken);
        var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
        var request = new CreateNodeEnrollmentRequest(
            new NodeEnrollmentRequest(
                pending.Id,
                options.Name,
                options.Roles,
                options.MaxConcurrentTasks,
                capabilities,
                pending.NodeToken!,
                CreateInventory()),
            pending.PairingKey!);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.PostAsJsonAsync(
            new Uri(new Uri(cluster.ApiBaseUrl), "api/v1/nodes/enrollment-requests"),
            request,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("La demande de jumelage auprès du cluster {ClusterId} a échoué avec HTTP {StatusCode}.", clusterId, (int)response.StatusCode);
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Trop de demandes de jumelage sont en attente sur ce cluster. Réessayez après leur approbation ou leur expiration."
                : $"Le serveur du cluster a refusé la demande de jumelage (HTTP {(int)response.StatusCode}).");
        }

        await identityStore.MarkPairingPublishedAsync(cancellationToken);
        logger.LogInformation("Une clé de jumelage a été créée pour le worker {NodeId} vers le cluster {ClusterId}.", pending.Id, cluster.Id);
        return new WorkerPairingResponse(pending.PairingKey!, pending.PairingExpiresAt!.Value);
    }

    private static NodeInventory CreateInventory() => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount,
        Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)));
}

public sealed record WorkerPairingResponse(string PairingKey, DateTimeOffset ExpiresAt);
