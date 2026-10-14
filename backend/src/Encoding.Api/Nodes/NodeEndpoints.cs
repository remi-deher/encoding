using Encoding.Api.Auth;
using Encoding.Api.Libraries;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Nodes;

public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        var nodes = app.MapGroup("/api/v1/nodes").WithTags("Nodes");
        nodes.MapPost("/enroll", async (
            NodeEnrollmentRequest request,
            NodeRepository repository,
            EnrollmentWindowRepository window,
            CancellationToken cancellationToken) =>
        {
            var errors = NodeHeartbeatValidator.Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            var windowOpen = (await window.GetAsync(cancellationToken)).IsOpen;
            var node = await repository.EnrollAsync(request, windowOpen, cancellationToken);
            if (node is not null) return Results.Ok(node);
            return windowOpen
                ? Results.Conflict(new { title = "Cet identifiant appartient déjà à un autre nœud." })
                : Results.Json(new { title = "L’enrôlement direct est fermé. Ouvrez la fenêtre d’enrôlement depuis l’interface ou utilisez une clé de jumelage." },
                    statusCode: StatusCodes.Status403Forbidden);
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("EnrollNode");

        nodes.MapPost("/enrollment-requests", async (
            CreateNodeEnrollmentRequest request,
            NodeRepository repository,
            AuthOptions authOptions,
            CancellationToken cancellationToken) =>
        {
            var errors = NodeHeartbeatValidator.Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await repository.CreatePairingRequestAsync(request, authOptions.MaxPendingPairingRequests, cancellationToken) switch
            {
                PairingRequestResult.Created => Results.Accepted(),
                PairingRequestResult.TooMany => Results.Json(new { title = "Trop de demandes de jumelage sont en attente. Approuvez ou laissez expirer les demandes existantes." },
                    statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.Conflict(new { title = "Une demande de jumelage active existe déjà pour une autre identité." }),
            };
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("CreateNodeEnrollmentRequest");

        nodes.MapGet("/enrollment-requests", async (
            NodeRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListPendingPairingRequestsAsync(cancellationToken)))
            .WithName("ListPendingNodeEnrollmentRequests");

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
            return node is null ? (IResult)Results.Unauthorized() : Results.Ok(node);
        }).AllowAnonymous().WithName("RecordNodeHeartbeat");

        nodes.MapGet("", async (NodeRepository repository, NodePresenceOptions options, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(options.OfflineAfter, cancellationToken)))
            .WithName("ListNodes");

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
                "busy"=>Results.Conflict(new{title="Ce nœud participe à un traitement ou une découverte. Attendez sa fin ou annulez les tâches avant de le supprimer."}),
                _=>Results.NotFound()
            };
        });
        nodes.MapGet("/{nodeId:guid}/activity",async(Guid nodeId,NodeRepository repository,CancellationToken ct)=>Results.Ok(await repository.ActivityAsync(nodeId,ct)));

        nodes.MapGet("/{nodeId:guid}", async (Guid nodeId, NodeRepository repository, NodePresenceOptions options, CancellationToken cancellationToken) =>
        {
            var node = await repository.GetAsync(nodeId, options.OfflineAfter, cancellationToken);
            return node is null ? (IResult)Results.NotFound() : Results.Ok(node);
        }).WithName("GetNode");
    }
}
