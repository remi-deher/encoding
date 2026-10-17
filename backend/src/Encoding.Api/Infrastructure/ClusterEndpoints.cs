using Encoding.Api.Auth;
using Encoding.Core.Nodes;

namespace Encoding.Api.Infrastructure;

using Encoding.Api.Infrastructure.PeerCluster;
using System.Text.Json;
using Encoding.Contracts.Cluster;

public sealed record ClusterNameRequest(string Name);
public sealed record ClusterJoinRequest(Guid ClusterId, string ClusterName, Guid NodeId, ClusterJoinBootstrap? Bootstrap = null);

public static class ClusterEndpoints
{
    public static void MapClusterEndpoints(this WebApplication app)
    {
        const string route = "/api/v1/cluster";
        app.MapGet(route, async (ClusterIdentityRepository repository, CancellationToken cancellationToken) =>
        {
            var identity = await repository.GetAsync(cancellationToken);
            return Results.Ok(new { identity.Id, identity.Name, identity.Standalone });
        }).AllowAnonymous().WithTags("Cluster").WithName("GetClusterIdentity");

        // Un nœud autonome devient un cluster en le nommant ; ensuite seul le nom change (architecture 0045).
        app.MapPost(route, async (ClusterNameRequest request, ClusterIdentityRepository repository, ClusterPeerAdmissionRepository admissions, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var peerOptions = services.GetRequiredService<PeerClusterOptions>();
            var peerConsensus = peerOptions.Enabled ? services.GetRequiredService<PeerConsensusService>() : null;
            if (!ClusterIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [$"Le nom du cluster est obligatoire ({ClusterIdentityRepository.MaximumNameLength} caractères au plus)."] });
            if (peerOptions.Enabled)
            {
                if (!peerOptions.Bootstrap && peerConsensus!.GetStatus().Members.Count < 2)
                    return Results.Conflict(new { title = "Ce pair doit rejoindre une membership existante avant de créer l’identité du cluster." });
                if (peerConsensus?.Get("cluster/name") is not null)
                    return Results.Conflict(new { title = "Ce cluster possède déjà un nom." });
                try
                {
                    await peerConsensus!.ReplicateAsync(new PeerReplicatedStateCommand(Guid.NewGuid(), "cluster/name", JsonSerializer.SerializeToElement(request.Name.Trim())), cancellationToken);
                    return Results.Ok(await repository.GetAsync(cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Le quorum Raft n’a pas confirmé le nom du cluster.");
                }
            }
            var current = await repository.GetAsync(cancellationToken);
            if (current.Standalone) await admissions.EnsureRootAdmissionAsync(current.Id, cancellationToken);
            return await repository.CreateAsync(request.Name, cancellationToken)
                ? Results.Ok(await repository.GetAsync(cancellationToken))
                : Results.Conflict(new { title = "Ce nœud fait déjà partie d’un cluster." });
        }).WithTags("Cluster").WithName("CreateCluster");

        app.MapPut(route, async (ClusterNameRequest request, ClusterIdentityRepository repository, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var peerOptions = services.GetRequiredService<PeerClusterOptions>();
            var peerConsensus = peerOptions.Enabled ? services.GetRequiredService<PeerConsensusService>() : null;
            if (!ClusterIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [$"Le nom du cluster est obligatoire ({ClusterIdentityRepository.MaximumNameLength} caractères au plus)."] });
            if (peerOptions.Enabled)
            {
                if (peerConsensus?.Get("cluster/name") is null)
                    return Results.Conflict(new { title = "Ce cluster n’a pas encore de nom." });
                try
                {
                    await peerConsensus!.ReplicateAsync(new PeerReplicatedStateCommand(Guid.NewGuid(), "cluster/name", JsonSerializer.SerializeToElement(request.Name.Trim())), cancellationToken);
                    return Results.Ok(await repository.GetAsync(cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Le quorum Raft n’a pas confirmé le nouveau nom du cluster.");
                }
            }
            return await repository.RenameAsync(request.Name, cancellationToken)
                ? Results.Ok(await repository.GetAsync(cancellationToken))
                : Results.Conflict(new { title = "Ce nœud est autonome : créez d’abord un cluster." });
        }).WithTags("Cluster").WithName("RenameCluster");

        // L'agent local peut aligner l'identité de sa base SQLite après l'approbation du cluster d'accueil.
        app.MapPost(route + "/join", async (
            ClusterJoinRequest request,
            IConfiguration configuration,
            HttpContext http,
            ClusterIdentityRepository repository,
            ClusterPeerAdmissionRepository admissions,
            CancellationToken cancellationToken) =>
        {
            if (!LocalEnrollment.Matches(configuration[LocalEnrollment.SecretFileSetting], http.Request.Headers[LocalEnrollment.HeaderName].ToString()))
                return Results.Unauthorized();
            if (request.ClusterId == Guid.Empty || !ClusterIdentityRepository.IsValidName(request.ClusterName))
                return Results.BadRequest(new { title = "Identité du cluster invalide." });
            if (request.NodeId == Guid.Empty || request.Bootstrap is null || request.Bootstrap.ClusterId != request.ClusterId
                || !string.Equals(request.Bootstrap.ClusterName, request.ClusterName, StringComparison.Ordinal))
                return Results.BadRequest(new { title = "La preuve d’admission du cluster est absente ou incohérente." });
            try { await admissions.ImportBootstrapAsync(request.Bootstrap, request.NodeId, cancellationToken); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or System.Security.Cryptography.CryptographicException or KeyNotFoundException)
            {
                return Results.BadRequest(new { title = "La signature ou la clé de données du cluster est invalide." });
            }
            await repository.AdoptAsync(request.ClusterId, request.ClusterName, cancellationToken);
            return Results.Ok(await repository.GetAsync(cancellationToken));
        })
        .AllowAnonymous()
        .RequireRateLimiting(AuthExtensions.AnonymousPolicy)
        .WithTags("Cluster")
        .WithName("AdoptJoinedClusterIdentity");
    }
}
