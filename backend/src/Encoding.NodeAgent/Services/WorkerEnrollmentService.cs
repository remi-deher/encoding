using System.Net.Http.Json;
using Encoding.Contracts.Nodes;

namespace Encoding.NodeAgent.Services;

public sealed class WorkerEnrollmentService(
    NodeAgentOptions options,
    WorkerIdentityStore identityStore,
    WorkerClusterDiscovery discovery,
    IToolCapabilityProbe capabilityProbe,
    ILogger<WorkerEnrollmentService> logger,
    Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions)
{
    public async Task<NodeResponse> EnrollAsync(Guid clusterId, CancellationToken cancellationToken)
    {
        var cluster = discovery.List().FirstOrDefault(candidate => candidate.Id == clusterId)
            ?? throw new InvalidOperationException("Ce cluster n’est plus visible sur le réseau. Actualisez la découverte et réessayez.");
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled)
            throw new InvalidOperationException("Ce worker est déjà rattaché à un cluster. Réinitialisez son identité locale pour le déplacer.");

        var pending = await identityStore.SaveEnrollmentAsync(cluster.Id.ToString("D"), cluster.Name, cluster.ApiBaseUrl, cancellationToken);
        var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
        var request = new NodeEnrollmentRequest(
            pending.Id,
            options.Name,
            options.Roles,
            options.MaxConcurrentTasks,
            capabilities,
            pending.NodeToken!);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.PostAsJsonAsync(
            new Uri(new Uri(cluster.ApiBaseUrl), "api/v1/nodes/enroll"),
            request,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("L’enrôlement auprès du cluster {ClusterId} a échoué avec HTTP {StatusCode}.", clusterId, (int)response.StatusCode);
            throw new HttpRequestException($"Le serveur du cluster a refusé l’enrôlement (HTTP {(int)response.StatusCode}).");
        }

        var node = await response.Content.ReadFromJsonAsync<NodeResponse>(jsonOptions.Value.SerializerOptions, cancellationToken)
            ?? throw new HttpRequestException("Le serveur n’a pas retourné la confirmation de l’enrôlement.");
        await identityStore.MarkEnrolledAsync(cancellationToken);
        logger.LogInformation("Le worker {NodeId} a rejoint le cluster {ClusterId}.", node.Id, cluster.Id);
        return node;
    }

    public async Task<WorkerPairingResponse> CreatePairingKeyAsync(Guid clusterId, CancellationToken cancellationToken)
    {
        var cluster = discovery.List().FirstOrDefault(candidate => candidate.Id == clusterId)
            ?? throw new InvalidOperationException("Ce cluster n’est plus visible sur le réseau. Actualisez la découverte et réessayez.");
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled)
            throw new InvalidOperationException("Ce worker est déjà rattaché à un cluster.");

        var pending = await identityStore.SavePairingAsync(cluster.Id.ToString("D"), cluster.Name, cluster.ApiBaseUrl, cancellationToken);
        var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
        var request = new CreateNodeEnrollmentRequest(
            new NodeEnrollmentRequest(
                pending.Id,
                options.Name,
                options.Roles,
                options.MaxConcurrentTasks,
                capabilities,
                pending.NodeToken!),
            pending.PairingKey!);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.PostAsJsonAsync(
            new Uri(new Uri(cluster.ApiBaseUrl), "api/v1/nodes/enrollment-requests"),
            request,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("La demande de jumelage auprès du cluster {ClusterId} a échoué avec HTTP {StatusCode}.", clusterId, (int)response.StatusCode);
            throw new HttpRequestException($"Le serveur du cluster a refusé la demande de jumelage (HTTP {(int)response.StatusCode}).");
        }

        await identityStore.MarkPairingPublishedAsync(cancellationToken);
        logger.LogInformation("Une clé de jumelage a été créée pour le worker {NodeId} vers le cluster {ClusterId}.", pending.Id, cluster.Id);
        return new WorkerPairingResponse(pending.PairingKey!, pending.PairingExpiresAt!.Value);
    }
}

public sealed record WorkerPairingResponse(string PairingKey, DateTimeOffset ExpiresAt);
