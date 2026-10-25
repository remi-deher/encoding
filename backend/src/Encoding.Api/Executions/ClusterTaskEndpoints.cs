using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
namespace Encoding.Api.Executions;

public static class ClusterTaskCoordinationEndpoints
{
    public static void MapClusterTaskCoordinationEndpoints(this WebApplication app)
    {
        app.MapPost("/"+ClusterTaskTransport.Path,async(HttpRequest request,HttpResponse response,ClusterIdentityRepository clusters,ClusterPeerAdmissionRepository admissions,ClusterPeerIdentityStore identity,
            ClusterTaskCoordinator coordinator,TaskOwnershipRepository ownership,TaskCheckpointRepository checkpoints,TaskCoordinationProjection projection,RuntimeArtifactStore artifacts,System.Data.Common.DbDataSource db,CancellationToken ct)=>
        {
            var cluster=await clusters.GetAsync(ct);var body=await ClusterTaskTransport.ReadAsync(request.Body,ct);if(body is null) return (IResult)Results.StatusCode(413);
            if(!Guid.TryParse(request.Headers[ClusterReconciliationSignature.PeerHeader],out var peer)||await admissions.GetActivePublicKeyAsync(cluster.Id,peer,ct) is not {} key
                ||!ClusterReconciliationSignature.Verify(key,peer,request.Headers[ClusterReconciliationSignature.TimeHeader],request.Headers[ClusterReconciliationSignature.SignatureHeader],body)) return Results.Unauthorized();
            ClusterTaskPeerRequest? message;try {message=JsonSerializer.Deserialize<ClusterTaskPeerRequest>(body,ClusterTaskTransport.Json);} catch(JsonException) {return Results.BadRequest();}
            if(message is not {Version:2}||message.ClusterId!=cluster.Id||message.Nonce==Guid.Empty) return Results.BadRequest();
            if(message.Kind=="blob")
            {
                if(message.Digest is null||!RuntimeArtifactStore.ValidDigest(message.Digest)) return Results.BadRequest();
                var allowed=false;
                await using(var known=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM execution_artifacts WHERE execution_id=@id AND sha256=@sha)")) {known.Parameters.AddWithValue("id",message.ExecutionId);known.Parameters.AddWithValue("sha",message.Digest);allowed=await known.ScalarBoolAsync(ct);}
                if(!allowed)
                {
                    await using var read=db.CreateCommand("SELECT body FROM task_checkpoints WHERE execution_id=@id ORDER BY epoch DESC LIMIT 1");read.Parameters.AddWithValue("id",message.ExecutionId);
                    if(await read.ExecuteScalarAsync(ct) is string signed) {var envelope=JsonSerializer.Deserialize<SignedTaskOwnershipPlan>(signed,TaskCheckpointRepository.Json)!;var e=JsonSerializer.Deserialize<ClusterTaskEvent>(envelope.Data,TaskCheckpointRepository.Json)!;allowed=checkpoints.Decode(e).Artifacts.Any(a=>a.Sha256==message.Digest);}
                }
                var path=artifacts.PathFor(message.ExecutionId,message.Digest);return allowed&&File.Exists(path)?Results.File(path,"application/octet-stream",enableRangeProcessing:true):Results.NotFound();
            }
            ClusterTaskPeerReply reply;
            if(message.Kind is "cancel" or "retry" or "resume")
            {
                ClusterTaskControlRequest? control;try {control=JsonSerializer.Deserialize<ClusterTaskControlRequest>(message.Payload??"",TaskCheckpointRepository.Json);} catch(JsonException) {return Results.BadRequest();}
                if(control is null||control.ExecutionId==Guid.Empty) return Results.BadRequest();
                var owner=await ownership.GetAsync(control.ExecutionId,ct);
                if(owner is null||owner.OwnerPeerId!=identity.PeerId) reply=new(2,cluster.Id,identity.PeerId,message.Nonce,false,Detail:"Propriétaire différent ; réconcilier avant de réessayer.");
                else
                {
                    var journal=new ExecutionRepository(db,ownership:ownership);Guid? created=null;bool success;
                    if(message.Kind=="cancel") success=await journal.CancelAsync(control.ExecutionId,ct,control.ExpectedRevision);
                    else {created=message.Kind=="retry"?await journal.RetryAsync(control.ExecutionId,ct):await journal.ResumeAsync(control.ExecutionId,ct);success=created is not null;}
                    if(success) request.HttpContext.RequestServices.GetService<ExecutionStreamBroadcaster>()?.Publish(control.ExecutionId);
                    reply=new(2,cluster.Id,identity.PeerId,message.Nonce,success,created?.ToString("D"));
                }
            }
            else if(message.Kind=="receive")
            {
                try {reply=await coordinator.ReceiveAsync(peer,message,ct);}
                catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or System.Security.Cryptography.CryptographicException or HttpRequestException)
                {reply=new(2,cluster.Id,identity.PeerId,message.Nonce,false,Detail:"Réception refusée : checkpoint ou artefacts indisponibles ou corrompus.");}
            }
            else if(message.Kind=="ready")
            {
                await projection.ProjectAsync(ct);
                await using var pending=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_ownership_inbox) OR EXISTS(SELECT 1 FROM task_coordination_inbox) OR EXISTS(SELECT 1 FROM cluster_sync_pending_projections)");
                reply=new(2,cluster.Id,identity.PeerId,message.Nonce,!await pending.ScalarBoolAsync(ct),JsonSerializer.Serialize(await new ClusterReconciliationRepository(db).GetVectorAsync(ct),TaskCheckpointRepository.Json));
            }
            else if(message.Kind=="activate" && message.Payload is {} payload)
            {
                await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);var(e,_)=await checkpoints.VerifyAsync(payload,c,tx,ct);
                if(e is null||e.AuthorPeerId!=peer||e.Kind!="activate") return Results.BadRequest();
                await ClusterReconciliationRepository.SetRegisterInTransactionAsync(TaskCheckpointRepository.Prefix+e.EventId.ToString("N"),payload,c,tx,ct);await tx.CommitAsync(ct);await projection.ProjectAsync(ct);reply=new(2,cluster.Id,identity.PeerId,message.Nonce,true);
            }
            else return Results.BadRequest();
            var result=JsonSerializer.SerializeToUtf8Bytes(reply,ClusterTaskTransport.Json);ClusterTaskTransport.SignResponse(response,identity,result);return Results.Bytes(result,"application/json");
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.ClusterMeshPolicy).WithTags("Cluster tasks"); // Self-authenticated admitted-peer RSA, as the mesh endpoint.
        var tasks=app.MapGroup("/api/v1/cluster/tasks").WithTags("Cluster tasks");
        tasks.MapPost("/{id:guid}/move",async(Guid id,ClusterTaskMoveRequest request,ClusterTaskCoordinator coordinator,CancellationToken ct)=>Result(await coordinator.MoveAsync(id,request.TargetPeerId,ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapPost("/{id:guid}/recover",async(Guid id,ClusterTaskCoordinator coordinator,CancellationToken ct)=>Result(await coordinator.RecoverAsync(id,ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapPost("/peers/{peer:guid}/neutralize",async(Guid peer,ClusterTaskNeutralizeRequest request,ClusterTaskCoordinator coordinator,CancellationToken ct)=>Result(await coordinator.NeutralizeAsync(peer,request,ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapPost("/peers/{peer:guid}/readmit",async(Guid peer,ClusterTaskCoordinator coordinator,CancellationToken ct)=>Result(await coordinator.ReadmitAsync(peer,ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapGet("/status",async(ClusterTaskMaintenance maintenance,CancellationToken ct)=>Results.Ok(await maintenance.StatusAsync(ct))).AllowViewer();
    }
    private static IResult Result(ClusterTaskResult result)=>result.Success?Results.Ok(result):Results.Conflict(result);
}
