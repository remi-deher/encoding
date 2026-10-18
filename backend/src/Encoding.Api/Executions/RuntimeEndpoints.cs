using Encoding.Api.Auth;
using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using Encoding.Api.Flows;
using Encoding.Api.Libraries;
using Encoding.Contracts.Libraries;
using Encoding.Contracts.Executions;
using Encoding.Api.Infrastructure.PeerCluster;
using Microsoft.AspNetCore.Http.Features;
using System.Reflection;

namespace Encoding.Api.Executions;

public static class RuntimeEndpoints
{
    public static void MapRuntimeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/executions/{id:guid}/artifacts",async(Guid id,RuntimeArtifactStore artifacts,CancellationToken ct)=>Results.Ok(await artifacts.ProducedAsync(id,ct)));
        app.MapGet("/api/v1/executions/{id:guid}/artifacts/{digest}",async(Guid id,string digest,RuntimeArtifactStore artifacts,CancellationToken ct)=>
        {
            if(!RuntimeArtifactStore.ValidDigest(digest)) return (IResult)Results.BadRequest();
            var artifact=(await artifacts.ProducedAsync(id,ct)).FirstOrDefault(item=>item.Sha256==digest);
            if(artifact is null) return Results.NotFound();
            var path=artifacts.PathFor(id,digest);
            return File.Exists(path)?Results.File(path,"application/octet-stream",artifact.Name,enableRangeProcessing:true):Results.NotFound();
        });
        app.MapPost("/api/v1/media-executions",()=>Results.Json(new{title="Lancez les traitements depuis les fichiers détectés de la bibliothèque."},statusCode:410));
        // Les workers s’authentifient par leur jeton de nœud (filtre ci-dessous), pas par une session utilisateur.
        var group=app.MapGroup("/api/v1/runtime/nodes/{nodeId:guid}").AllowAnonymous().WithTags("Runtime");
        group.AddEndpointFilter(async(context,next)=>
        {
            var node=Guid.Parse((string)context.HttpContext.Request.RouteValues["nodeId"]!);
            var runtime=context.HttpContext.RequestServices.GetRequiredService<RuntimeRepository>();
            if(!await runtime.AuthenticateAsync(node,context.HttpContext.Request.Headers["X-Node-Token"].ToString(),context.HttpContext.RequestAborted)) return Results.Unauthorized();
            return await next(context);
        });
        group.MapPost("/ai/generate", async (AiGenerateRequest request, AiClusterService ai, CancellationToken ct) =>
            Results.Ok(await ai.GenerateAsync(request.Provider, request.Prompt, ct)))
            .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
                return await next(context);
            var services = context.HttpContext.RequestServices;
            if (services.GetService<PeerClusterOptions>() is not { Enabled: true }) return await next(context);
            if (!services.GetRequiredService<PeerConsensusService>().TryGetLeaderLease(out var leaderLease))
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Le pair n’a pas de bail Raft valide; l’opération worker est suspendue.");
            var assignmentGate = services.GetRequiredService<IPeerAssignmentGate>();
            if (ReadLeaseEpochs(context.Arguments).Any(epoch => !assignmentGate.IsCurrentFencingEpoch(epoch)))
                return Results.Conflict(new { title = "Le bail worker appartient à un ancien terme Raft et ne peut plus modifier cette exécution." });

            var originalToken = context.HttpContext.RequestAborted;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(originalToken, leaderLease);
            context.HttpContext.RequestAborted = linked.Token;
            for (var index = 0; index < context.Arguments.Count; index++)
                if (context.Arguments[index] is CancellationToken) context.Arguments[index] = linked.Token;
            try { return await next(context); }
            catch (OperationCanceledException) when (!originalToken.IsCancellationRequested && leaderLease.IsCancellationRequested)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Le bail Raft a expiré pendant l’opération worker.");
            }
            finally { context.HttpContext.RequestAborted = originalToken; }
        });
        group.MapGet("/capacity",async(Guid nodeId,LibraryProcessingRepository repository,CancellationToken ct)=>
            await repository.NodeAsync(nodeId,ct) is {} settings?(IResult)Results.Ok(new Encoding.Contracts.Nodes.RuntimeCapacity(settings.EffectiveMaxConcurrentTasks)):Results.NotFound());
        group.MapPost("/library-scans/claim",async(Guid nodeId,LibraryWorkflowRepository repository,CancellationToken ct)=>
            await repository.ClaimScanAsync(nodeId,ct) is {} scan?(IResult)Results.Ok(scan):Results.NoContent());
        group.MapPost("/library-scans/{id:guid}/renew",async(Guid nodeId,Guid id,LibraryScanFence request,LibraryWorkflowRepository repository,CancellationToken ct)=>
            await repository.RenewScanAsync(id,nodeId,request.Epoch,ct)?(IResult)Results.NoContent():Results.Conflict());
        group.MapPost("/library-scans/{id:guid}/complete",async(Guid nodeId,Guid id,CompleteLibraryScan request,LibraryWorkflowRepository repository,CancellationToken ct)=>
        {
            try {return await repository.CompleteScanAsync(id,nodeId,request,ct)?(IResult)Results.NoContent():Results.Conflict();}
            catch(ArgumentException) {return Results.BadRequest();}
        });
        group.MapPost("/claim",async(Guid nodeId,RuntimeRepository runtime,CancellationToken ct)=>
            await runtime.ClaimAsync(nodeId,ct) is { } lease ? (IResult)Results.Ok(lease) : Results.NoContent());
        group.MapPost("/executions/{id:guid}/renew",async(Guid nodeId,Guid id,RuntimeFence request,RuntimeRepository runtime,CancellationToken ct)=>
            await runtime.RenewAsync(id,nodeId,request.Epoch,ct) is { } status ? (IResult)Results.Ok(status) : Results.Conflict());
        group.MapPost("/executions/{id:guid}/disk-locks/acquire",async(Guid nodeId,Guid id,DiskLockRequest request,DiskLockRepository locks,CancellationToken ct)=>
        {
            try {return await locks.TryAcquireAsync(id,nodeId,request,ct) is { } response?(IResult)Results.Ok(response):Results.Conflict();}
            catch(ArgumentException) {return Results.BadRequest();}
        });
        group.MapPost("/executions/{id:guid}/disk-locks/release",async(Guid nodeId,Guid id,DiskLockRequest request,DiskLockRepository locks,CancellationToken ct)=>
        {
            try {return await locks.ReleaseAsync(id,nodeId,request,ct) is not null?(IResult)Results.NoContent():Results.Conflict();}
            catch(ArgumentException) {return Results.BadRequest();}
        });
        group.MapPost("/executions/{id:guid}/steps/start",async(Guid nodeId,Guid id,StartRuntimeStep request,RuntimeRepository runtime,CancellationToken ct)=>
            await runtime.StartStepAsync(id,nodeId,request,ct) is { } step ? (IResult)Results.Ok(step) : Results.Conflict());
        group.MapPost("/executions/{id:guid}/steps/complete",async(Guid nodeId,Guid id,CompleteRuntimeStep request,RuntimeRepository runtime,CancellationToken ct)=>
            await runtime.CompleteStepAsync(id,nodeId,request,ct) ? (IResult)Results.NoContent() : Results.Conflict());
        group.MapPost("/executions/{id:guid}/wait/probe",async(Guid nodeId,Guid id,ReportWaitProbe request,ExecutionWaitRepository waits,CancellationToken ct)=>
            await waits.ReportProbeAsync(id,nodeId,request,ct) ? (IResult)Results.NoContent() : Results.Conflict());
        group.MapPost("/executions/{id:guid}/finish",async(Guid nodeId,Guid id,FinishRuntimeExecution request,RuntimeTransferRepository transfers,CancellationToken ct)=>
            await transfers.FinishAsync(id,nodeId,request,ct) ? (IResult)Results.NoContent() : Results.Conflict());
        group.MapGet("/transfers",async(Guid nodeId,RuntimeTransferRepository transfers,Encoding.Api.Plugins.PublicationGuardProvider guards,CancellationToken ct)=>
        {
            var list=await transfers.ListAsync(nodeId,ct);
            static bool Replaces(RuntimeTransfer item)=>item.Publish && item.ReplacementGraceHours is not null;
            if(!list.Any(Replaces)) return Results.Ok(list);
            var active=await guards.ListAsync(ct);
            return Results.Ok(active.Count==0?list:list.Select(item=>Replaces(item)?item with { Guards=active }:item).ToArray());
        });
        group.MapGet("/executions/{id:guid}/source",async(Guid nodeId,Guid id,long epoch,RuntimeArtifactStore artifacts,RuntimeRepository runtime,CancellationToken ct)=>
            await artifacts.AuthorizeAsync(id,nodeId,epoch,ct) ? (IResult)Results.Ok(await runtime.SourceAsync(id,ct)) : Results.Conflict());
        group.MapPost("/executions/{id:guid}/publication",async(Guid nodeId,Guid id,ConfirmRuntimePublication request,RuntimeTransferRepository transfers,CancellationToken ct)=>
            await transfers.ConfirmAsync(id,nodeId,request,ct) ? (IResult)Results.NoContent() : Results.Conflict());
        group.MapGet("/replacements",async(Guid nodeId,SourceReplacementRepository replacements,CancellationToken ct)=>Results.Ok(await replacements.TasksAsync(nodeId,ct)));
        group.MapPost("/replacements/{id:guid}/report",async(Guid nodeId,Guid id,ReportReplacementTask request,SourceReplacementRepository replacements,CancellationToken ct)=>
            await replacements.ReportAsync(id,nodeId,request,ct) ? (IResult)Results.NoContent() : Results.Conflict());
        group.MapPost("/executions/{id:guid}/source-failure",async(Guid nodeId,Guid id,FinishRuntimeExecution request,RuntimeTransferRepository transfers,CancellationToken ct)=>
            await transfers.FinishAsync(id,nodeId,request,ct,true) ? (IResult)Results.NoContent() : Results.Conflict());
        group.MapPut("/executions/{id:guid}/dav/{digest}",async(Guid nodeId,Guid id,string digest,long epoch,bool? original,string? inputPath,HttpRequest request,RuntimeArtifactStore artifacts,IConfiguration configuration,CancellationToken ct)=>
        {
            if(!RuntimeArtifactStore.ValidDigest(digest)) return (IResult)Results.BadRequest();
            var feature=request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if(feature is { IsReadOnly:false }) feature.MaxRequestBodySize=configuration.GetValue<long>("Runtime:MaxArtifactBytes",1024L*1024*1024*1024);
            try { return await artifacts.PutAsync(id,nodeId,epoch,digest,request.Body,original==true,ct,inputPath) ? Results.Created(request.Path,null) : Results.Conflict(); }
            catch(InvalidDataException e) { return Results.BadRequest(new { title=e.Message }); }
        });
        group.MapGet("/executions/{id:guid}/dav/{digest}",async(Guid nodeId,Guid id,string digest,long epoch,RuntimeArtifactStore artifacts,CancellationToken ct)=>
        {
            if(!RuntimeArtifactStore.ValidDigest(digest) || !await artifacts.AuthorizeAsync(id,nodeId,epoch,ct)) return (IResult)Results.Conflict();
            var path=artifacts.PathFor(id,digest);
            return File.Exists(path) ? Results.File(path,"application/octet-stream",enableRangeProcessing:true,entityTag:new Microsoft.Net.Http.Headers.EntityTagHeaderValue('"'+digest+'"')) : Results.NotFound();
        });
        group.MapMethods("/executions/{id:guid}/dav/{digest}",["PROPFIND"],async(Guid nodeId,Guid id,string digest,long epoch,RuntimeArtifactStore artifacts,CancellationToken ct)=>
        {
            if(!RuntimeArtifactStore.ValidDigest(digest) || !await artifacts.AuthorizeAsync(id,nodeId,epoch,ct)) return (IResult)Results.Conflict();
            var file=new FileInfo(artifacts.PathFor(id,digest));
            if(!file.Exists) return Results.NotFound();
            return Results.Text($"<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{digest}</d:href><d:propstat><d:prop><d:getcontentlength>{file.Length}</d:getcontentlength><d:getetag>\"{digest}\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>","application/xml",statusCode:207);
        });
    }

    private static IEnumerable<long> ReadLeaseEpochs(IEnumerable<object?> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument is long value)
            {
                yield return value;
                continue;
            }

            if (argument is null) continue;
            var property = argument.GetType().GetProperty("Epoch", BindingFlags.Instance | BindingFlags.Public);
            if (property?.PropertyType == typeof(long) && property.GetValue(argument) is long epoch) yield return epoch;
        }
    }
}
