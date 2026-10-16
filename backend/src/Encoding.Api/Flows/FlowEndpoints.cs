using Encoding.Api.Auth;
using Encoding.Api.Infrastructure.PeerCluster;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowEndpoints
{
    public static void MapFlowEndpoints(this WebApplication app)
    {
        var flows = app.MapGroup("/api/v1/flows").WithTags("Flows");
        flows.MapGet("/{id:guid}/edit",async(Guid id,FlowRepository repository,CancellationToken ct)=>
        {
            if(await repository.DraftAsync(id,ct) is { } draft) return (IResult)Results.Ok(draft);
            return await repository.GetAsync(id,null,ct) is { } saved ? Results.Ok(saved) : Results.NotFound();
        });
        flows.MapPut("/{id:guid}/draft",async(Guid id,SaveFlowDraftRequest request,FlowRepository repository,FlowDefinitionValidator validator,CancellationToken ct)=>
        {
            var d=request.Definition;
            if(d is null || d.Id!=id || id==Guid.Empty || request.ExpectedRevision<0 || request.ExpectedRevision==int.MaxValue || d.FormatVersion!=1 || d.Version<1
                || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length>128 || d.MediaType is not("video" or "image" or "music" or "subtitles")
                || d.Nodes is null || d.Connections is null || d.Nodes.Count>1000 || d.Connections.Count>4000
                || JsonSerializer.Serialize(d,FlowRepository.JsonOptions).Length>1048576)
                return (IResult)Results.BadRequest(new { title="Le document de brouillon est invalide." });
            var validation=validator.ValidateDraft(d); if(!validation.IsValid) return Results.UnprocessableEntity(validation);
            return await repository.SaveDraftAsync(request,ct) is { } saved ? Results.Ok(saved) : Results.Conflict(new { title="Le brouillon a été modifié ailleurs." });
        });
        flows.MapGet("/{id:guid}/export", async (Guid id,FlowRepository repository,CancellationToken ct)=>
        {
            if(await repository.DraftAsync(id,ct) is { } draft) return (IResult)Results.File(JsonSerializer.SerializeToUtf8Bytes(new {kind="draft",draftRevision=draft.DraftRevision,definition=draft.Definition},FlowRepository.JsonOptions),"application/json",$"{id:D}.draft.json");
            return await repository.GetAsync(id,null,ct) is { } saved ? Results.File(JsonSerializer.SerializeToUtf8Bytes(saved.Definition,FlowRepository.JsonOptions),"application/json",$"{id:D}.v{saved.Definition.Version}.json") : Results.NotFound();
        });
        flows.MapGet("/{id:guid}/versions", async(Guid id,FlowRepository repository,CancellationToken ct)=>Results.Ok(await repository.VersionsAsync(id,ct)));
        flows.MapPost("/export",async(Guid[] ids,FlowArchiveService archives,CancellationToken ct)=>
        {
            try { var bytes=await archives.ExportAsync(ids,ct); return bytes is null ? (IResult)Results.BadRequest(new { title="Sélection invalide ou flow introuvable." }) : Results.File(bytes,"application/zip","flows.zip"); }
            catch(InvalidDataException e) { return Results.BadRequest(new { title=e.Message }); }
        }).AllowViewer();
        flows.MapPost("/import",async(HttpRequest request,FlowArchiveService archives,PeerClusterOptions peerOptions,CancellationToken ct)=>
        {
            if (peerOptions.Enabled) return Results.Conflict(new { title="L’import groupé des flows sera disponible quand il utilisera une commande Raft atomique." });
            try
            {
                var (result,conflict,count)=await archives.ImportAsync(request.Body,request.ContentType=="application/zip",ct);
                if(!result.IsValid) return (IResult)Results.UnprocessableEntity(result);
                return conflict ? Results.Conflict(new { title="Une version existante a un contenu différent. Aucun flow n’a été importé." }) : Results.Ok(new { imported=count });
            }
            catch(Exception e) when(e is InvalidDataException or JsonException) { return Results.BadRequest(new { title="Fichier JSON ou ZIP invalide ou trop volumineux." }); }
        });
        flows.MapGet("", async (FlowRepository repository, CancellationToken ct) => Results.Ok(await repository.ListAsync(ct)));
        flows.MapGet("/{id:guid}", async (Guid id, FlowRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id,null,ct) is { } flow ? (IResult)Results.Ok(flow) : Results.NotFound());
        flows.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version, FlowRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id,version,ct) is { } flow ? (IResult)Results.Ok(flow) : Results.NotFound());
        flows.MapPut("/{id:guid}", async (Guid id, SaveFlowRequest request, FlowDefinitionValidator validator, FlowRepository repository, CancellationToken ct) =>
        {
            if (request.Definition is null || id != request.Definition.Id || request.ExpectedVersion < 0
                || request.ExpectedVersion == int.MaxValue || request.Definition.Version != request.ExpectedVersion + 1)
                return (IResult)Results.BadRequest(new { title="Identifiant ou version attendue invalide." });
            var (_,validation)=validator.ValidateDocument(JsonSerializer.SerializeToElement(request.Definition,FlowRepository.JsonOptions));
            if (!validation.IsValid) return Results.UnprocessableEntity(validation);
            if (await repository.SaveAsync(request,ct) is not { } saved) return Results.Conflict(new { title="Une autre version a été enregistrée. Rechargez le flow avant de publier vos modifications." });
            return Results.Ok(saved);
        });
        flows.MapGet("/components", (FlowComponentRegistry registry) => Results.Ok(registry.Plugins))
            .WithName("GetFlowComponentCatalog");
        flows.MapPost("/validate", (JsonElement document, FlowDefinitionValidator validator) =>
        {
            var (_, result) = validator.ValidateDocument(document);
            return result.IsValid ? (IResult)Results.Ok(result) : Results.UnprocessableEntity(result);
        }).AllowViewer().WithName("ValidateFlowDefinition");
    }
}
