using Encoding.Api.Auth;
using Encoding.Api.Libraries;
using Encoding.Contracts.Nodes;
using Encoding.Api.Infrastructure;
using Encoding.Core.Nodes;

namespace Encoding.Api.Nodes;

public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        var nodes = app.MapGroup("/api/v1/nodes").WithTags("Nodes");
        nodes.MapPost("/{nodeId:guid}/directory-browse", async (
            Guid nodeId, NodeDirectoryBrowserRequest request, NodeDirectoryBrowserRepository browser, CancellationToken ct) =>
        {
            if (await browser.CreateAsync(nodeId, request.Path, ct) is not { } id) return Results.NotFound();
            return Results.Accepted($"/api/v1/nodes/{nodeId:D}/directory-browse/{id:D}", new { id });
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy)
          .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy)
          .WithName("RequestNodeDirectoryBrowse");
        nodes.MapGet("/{nodeId:guid}/directory-browse/{requestId:guid}", async (
            Guid nodeId, Guid requestId, NodeDirectoryBrowserRepository browser, CancellationToken ct) =>
        {
            var result = await browser.GetAsync(requestId, nodeId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy).WithName("GetNodeDirectoryBrowse");
        nodes.MapPost("/enroll", async (
            NodeEnrollmentRequest request,
            NodeRepository repository,
            EnrollmentWindowRepository window,
            LocalNodeRegistry localNode,
            ClusterIdentityRepository clusterIdentity,
            ClusterDataKeyStore dataKeyStore,
            ClusterPeerAdmissionRepository peerAdmissions,
            ClusterReconciliationRepository reconciliationRepository,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var errors = NodeHeartbeatValidator.Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            if (request.ClusterPeerId is not null)
            {
                var identity = await clusterIdentity.GetAsync(cancellationToken);
                if (!dataKeyStore.HasKey(identity.Id))
                    await peerAdmissions.EnsureRootAdmissionAsync(identity.Id, cancellationToken);
            }
            // Le nœud local de l'image unique prouve son appartenance au conteneur par un secret de fichier : il est enrôlé d'office.
            var isLocal = LocalEnrollment.Matches(configuration[LocalEnrollment.SecretFileSetting], httpContext.Request.Headers[LocalEnrollment.HeaderName].ToString());
            var windowOpen = isLocal || (await window.GetAsync(cancellationToken)).IsOpen;
            var node = await repository.EnrollAsync(request, windowOpen, cancellationToken);
            if (node is not null && isLocal) await localNode.RegisterAsync(node.Id, cancellationToken);
            if (node is not null)
            {
                if (request.ClusterPeerId is { } peerId && request.ClusterPeerPublicKey is { } publicKey)
                    await peerAdmissions.CreateDirectApprovalGrantAsync(node.Id, peerId, publicKey, cancellationToken);
                if (request.ClusterPeerId is not null && request.ClusterPeerPublicKey is not null && !string.IsNullOrWhiteSpace(request.ClusterPeerReconciliationUrl))
                    await reconciliationRepository.AddPeerEndpointAsync(request.ClusterPeerReconciliationUrl, "enroll", cancellationToken);
                return Results.Ok(node with { ClusterBootstrap = await peerAdmissions.GetBootstrapForNodeAsync(node.Id, cancellationToken) });
            }
            return windowOpen
                ? Results.Conflict(new { title = "Cet identifiant appartient déjà à un autre nœud." })
                : Results.Json(new { title = "L’enrôlement direct est fermé. Ouvrez la fenêtre d’enrôlement depuis l’interface ou utilisez une clé de jumelage." },
                    statusCode: StatusCodes.Status403Forbidden);
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("EnrollNode");

        nodes.MapPost("/enrollment-requests", async (
            CreateNodeEnrollmentRequest request,
            NodeRepository repository,
            ClusterIdentityRepository clusterIdentity,
            ClusterDataKeyStore dataKeyStore,
            ClusterPeerAdmissionRepository peerAdmissions,
            AuthOptions authOptions,
            FirstLinkRepository firstLink,
            ILogger<FirstLinkRepository> logger,
            CancellationToken cancellationToken) =>
        {
            var errors = NodeHeartbeatValidator.Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            if (request.Node.ClusterPeerId is not null)
            {
                var identity = await clusterIdentity.GetAsync(cancellationToken);
                if (!dataKeyStore.HasKey(identity.Id))
                    await peerAdmissions.EnsureRootAdmissionAsync(identity.Id, cancellationToken);
            }
            return await repository.CreatePairingRequestAsync(request, authOptions.MaxPendingPairingRequests, cancellationToken) switch
            {
                PairingRequestResult.Created => await AcceptIfFirstLinkAsync(),
                PairingRequestResult.TooMany => Results.Json(new { title = "Trop de demandes de jumelage sont en attente. Approuvez ou laissez expirer les demandes existantes." },
                    statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.Conflict(new { title = "Une demande de jumelage active existe déjà pour une autre identité." }),
            };

            // Mode « première liaison » : la demande est approuvée sans vérification, une seule fois, et le mode se désactive.
            async Task<IResult> AcceptIfFirstLinkAsync()
            {
                if (await firstLink.TryConsumeAsync(cancellationToken) is { } administrator
                    && await repository.ApprovePairingRequestAsync(request.Node.Id, cancellationToken) is not null)
                    logger.LogWarning("Le nœud {NodeId} ({NodeName}) a été accepté sans vérification au titre de la première liaison activée par {Administrator}.",
                        request.Node.Id, request.Node.Name, administrator);
                return Results.Accepted();
            }
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("CreateNodeEnrollmentRequest");

        nodes.MapGet("/enrollment-requests", async (
            NodeRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListPendingPairingRequestsAsync(cancellationToken)))
            .WithName("ListPendingNodeEnrollmentRequests");

        nodes.MapGet("/{nodeId:guid}/cluster-bootstrap", async (
            Guid nodeId,
            NodeRepository repository,
            ClusterPeerAdmissionRepository peerAdmissions,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var token = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token) || !await repository.AuthenticateTokenAsync(nodeId, token, cancellationToken))
                return (IResult)Results.Unauthorized();
            var bootstrap = await peerAdmissions.GetBootstrapForNodeAsync(nodeId, cancellationToken);
            return bootstrap is null
                ? Results.NotFound(new { title = "Aucune admission de serveur n’est disponible pour ce nœud." })
                : Results.Ok(bootstrap);
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("GetClusterBootstrapForNode");

        nodes.MapPost("/enrollment-requests/{nodeId:guid}/approve", async (
            Guid nodeId,
            NodeRepository repository,
            CancellationToken cancellationToken) =>
        {
            var node = await repository.ApprovePairingRequestAsync(nodeId, cancellationToken);
            return node is null
                ? (IResult)Results.NotFound(new { title = "Cette demande est expirée ou n’existe plus." })
                : Results.Ok(node);
        }).WithName("ApprovePendingNodeEnrollmentRequest");

        nodes.MapPost("/enrollment-requests/approve", async (
            ApproveNodeEnrollmentRequest request,
            NodeRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = NodeHeartbeatValidator.Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            var node = await repository.ApprovePairingRequestAsync(request.PairingKey, cancellationToken);
            return node is null
                ? Results.NotFound(new { title = "Cette clé de jumelage est invalide, expirée ou déjà utilisée." })
                : Results.Ok(node);
        }).WithName("ApproveNodeEnrollmentRequest");

        nodes.MapPut("/{nodeId:guid}/heartbeat", async (
            Guid nodeId,
            NodeHeartbeatRequest request,
            NodeRepository repository,
            LocalNodeRegistry localNode,
            ClusterReconciliationRepository reconciliationRepository,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var providedToken = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (string.IsNullOrWhiteSpace(providedToken))
            {
                return (IResult)Results.Unauthorized();
            }

            var errors = NodeHeartbeatValidator.Validate(request);
            if (errors.Count > 0)
            {
                return (IResult)Results.ValidationProblem(errors);
            }

            var node = await repository.RecordHeartbeatAsync(nodeId, providedToken, request, cancellationToken);
            if (node is not null)
            {
                if (LocalEnrollment.Matches(configuration[LocalEnrollment.SecretFileSetting], httpContext.Request.Headers[LocalEnrollment.HeaderName].ToString()))
                    await localNode.RegisterAsync(node.Id, cancellationToken);
                var serverUrls = new List<string>();
                if (ClusterReconciliationRepository.NormalizeUrl(configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]) is { } advertised)
                    serverUrls.Add(advertised);
                foreach (var peerUrl in await reconciliationRepository.GetPeerEndpointsAsync(cancellationToken))
                {
                    if (!serverUrls.Contains(peerUrl, StringComparer.OrdinalIgnoreCase))
                        serverUrls.Add(peerUrl);
                }
                return Results.Ok(node with { ClusterServerUrls = serverUrls });
            }
            // 410 : l'identité a été retirée du cluster ; le jeton est effacé à ce retrait, donc seul l'identifiant permet de le reconnaître.
            // Le nœud abandonne alors cette identité au lieu de rester « enrôlé » sans jamais être vu.
            return await repository.IsRemovedAsync(nodeId, cancellationToken)
                ? Results.Json(new { title = "Ce nœud a été retiré du cluster." }, statusCode: StatusCodes.Status410Gone)
                : Results.Unauthorized();
        }).AllowAnonymous().WithName("RecordNodeHeartbeat");

        nodes.MapPost("/{nodeId:guid}/leave", async (Guid nodeId, NodeRepository repository, HttpContext httpContext, CancellationToken ct) =>
        {
            var token = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token)) return (IResult)Results.Unauthorized();
            return await repository.LeaveAsync(nodeId, token, ct) switch
            {
                "deleted" => Results.NoContent(),
                "coordination" => Results.Conflict(new {title="Départ du serveur différé : réconcilier les pairs et déplacer ou terminer ses tâches."}),
                "busy" => Results.Conflict(new { title = "Ce nœud participe à un traitement ou une découverte. Attendez sa fin avant de quitter le cluster." }),
                _ => Results.Unauthorized()
            };
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("LeaveCluster");

        nodes.MapGet("", async (NodeRepository repository, NodePresenceOptions options, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(options.OfflineAfter, cancellationToken)))
            .WithName("ListNodes");

        nodes.MapPut("/{nodeId:guid}/mode", async (Guid nodeId, ChangeNodeModeRequest request, NodeRepository repository, NodePresenceOptions options, CancellationToken ct) =>
        {
            if (!request.Server && !request.Worker)
                return (IResult)Results.BadRequest(new { title = "Le nœud doit garder au moins une fonction : serveur ou worker." });
            return await repository.RequestModeAsync(nodeId, request.Server, request.Worker, options.OfflineAfter, ct) switch
            {
                NodeRepository.ModeRequestResult.NotFound => Results.NotFound(),
                NodeRepository.ModeRequestResult.CoordinationUnavailable => Results.Conflict(new {title="Modification de topologie différée : réconcilier les serveurs et vider les files possédées."}),
                NodeRepository.ModeRequestResult.LastServer => Results.Conflict(new { title = "Ce nœud est le dernier serveur en ligne : sa fonction serveur ne peut pas être retirée." }),
                _ => Results.Accepted()
            };
        }).WithName("RequestNodeMode");

        nodes.MapPut("/{nodeId:guid}/draining", async (Guid nodeId, DrainNodeRequest request, NodeRepository repository, CancellationToken ct) =>
            await repository.SetDrainingAsync(nodeId, request.Draining, ct) ? Results.Accepted() : Results.NotFound())
            .WithName("SetNodeDraining");

        nodes.MapPost("/{nodeId:guid}/quarantine/clear", async (Guid nodeId, NodeRepository repository, CancellationToken ct) =>
            await repository.ClearQuarantineAsync(nodeId, ct) ? Results.Accepted() : Results.NotFound(new { title = "Ce nœud n'est pas en quarantaine." }))
            .WithName("ClearNodeQuarantine");

        nodes.MapGet("/{nodeId:guid}/processing",async(Guid nodeId,LibraryProcessingRepository repository,CancellationToken ct)=>
            await repository.NodeAsync(nodeId,ct) is {} settings?(IResult)Results.Ok(settings):Results.NotFound());
        nodes.MapPut("/{nodeId:guid}/processing",async(Guid nodeId,SaveNodeProcessingSettings request,LibraryProcessingRepository repository,CancellationToken ct)=>
        {
            try{return await repository.SaveNodeAsync(nodeId,request,ct)?(IResult)Results.NoContent():Results.Conflict(new{title="La configuration du nœud a changé."});}
            catch(ArgumentException e){return Results.BadRequest(new{title=e.Message});}
        });

        nodes.MapDelete("/{nodeId:guid}",async(Guid nodeId,NodeRepository repository,CancellationToken ct)=>
        {
            return await repository.DeleteAsync(nodeId,ct) switch {
                "deleted"=>(IResult)Results.NoContent(),
                "coordination"=>Results.Conflict(new{title="Retrait différé : réconcilier les serveurs du cluster."}),
                "busy"=>Results.Conflict(new{title="Ce nœud participe à un traitement ou une découverte. Attendez sa fin ou annulez les tâches avant de le supprimer."}),
                _=>Results.NotFound()
            };
        });
        nodes.MapGet("/activity", async (NodeRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.ActiveTaskCountsAsync(ct)).ToDictionary(item => item.Key.ToString("D"), item => item.Value)))
            .WithName("ListNodesActiveTasks");
        nodes.MapGet("/{nodeId:guid}/activity",async(Guid nodeId,NodeRepository repository,CancellationToken ct)=>Results.Ok(await repository.ActivityAsync(nodeId,ct)));

        nodes.MapGet("/{nodeId:guid}", async (Guid nodeId, NodeRepository repository, NodePresenceOptions options, CancellationToken cancellationToken) =>
        {
            var node = await repository.GetAsync(nodeId, options.OfflineAfter, cancellationToken);
            return node is null ? (IResult)Results.NotFound() : Results.Ok(node);
        }).WithName("GetNode");
    }
}
