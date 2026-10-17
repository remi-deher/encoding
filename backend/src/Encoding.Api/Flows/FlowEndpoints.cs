using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.PeerCluster;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowEndpoints
{
    public static void MapFlowEndpoints(this WebApplication app)
    {
        var flows = app.MapGroup("/api/v1/flows").WithTags("Flows");
        flows.MapGet("/{id:guid}/edit", async (Guid id, FlowRepository repository, CancellationToken ct) =>
        {
            if (await repository.DraftAsync(id, ct) is { } draft) return (IResult)Results.Ok(draft);
            return await repository.GetAsync(id, null, ct) is { } saved ? Results.Ok(saved) : ApiProblem.NotFound();
        }).WithName("GetFlowForEdit");

        flows.MapPut("/{id:guid}/draft", async (Guid id, SaveFlowDraftRequest request, FlowRepository repository, FlowDefinitionValidator validator, CancellationToken ct) =>
        {
            var validation = validator.ValidateDraft(request.Definition);
            if (!validation.IsValid) return (IResult)Results.UnprocessableEntity(validation);
            return await repository.SaveDraftAsync(request, ct) is { } saved
                ? Results.Ok(saved)
                : ApiProblem.Conflict("Le brouillon a été modifié ailleurs.");
        }).AddEndpointFilter<ValidateFlowDraftFilter>().WithName("SaveFlowDraft");

        flows.MapGet("/{id:guid}/export", async (Guid id, FlowRepository repository, CancellationToken ct) =>
        {
            if (await repository.DraftAsync(id, ct) is { } draft)
                return (IResult)Results.File(JsonSerializer.SerializeToUtf8Bytes(new { kind = "draft", draftRevision = draft.DraftRevision, definition = draft.Definition }, FlowRepository.JsonOptions), "application/json", $"{id:D}.draft.json");
            return await repository.GetAsync(id, null, ct) is { } saved
                ? Results.File(JsonSerializer.SerializeToUtf8Bytes(saved.Definition, FlowRepository.JsonOptions), "application/json", $"{id:D}.v{saved.Definition.Version}.json")
                : ApiProblem.NotFound();
        }).WithName("ExportSingleFlow");

        flows.MapGet("/{id:guid}/versions", async (Guid id, FlowRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.VersionsAsync(id, ct)))
            .WithName("ListFlowVersions");

        flows.MapPost("/export", async (Guid[] ids, FlowArchiveService archives, CancellationToken ct) =>
        {
            try
            {
                var bytes = await archives.ExportAsync(ids, ct);
                return bytes is null
                    ? ApiProblem.BadRequest("Sélection invalide ou flow introuvable.")
                    : Results.File(bytes, "application/zip", "flows.zip");
            }
            catch (InvalidDataException e) { return ApiProblem.BadRequest(e.Message); }
        }).AllowViewer().RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy).WithName("ExportFlowsArchive");

        flows.MapPost("/import", async (HttpRequest request, FlowArchiveService archives, PeerClusterOptions peerOptions, CancellationToken ct) =>
        {
            try
            {
                var (result, conflict, count) = await archives.ImportAsync(request.Body, request.ContentType == "application/zip", ct);
                if (!result.IsValid) return (IResult)Results.UnprocessableEntity(result);
                return conflict
                    ? ApiProblem.Conflict("Une version existante a un contenu différent. Aucun flow n’a été importé.")
                    : Results.Ok(new { imported = count });
            }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            {
                return ApiProblem.BadRequest("Fichier JSON ou ZIP invalide ou trop volumineux.");
            }
        }).RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy).WithName("ImportFlowsArchive");

        flows.MapGet("", async (FlowRepository repository, CancellationToken ct) => Results.Ok(await repository.ListAsync(ct)))
            .WithName("ListFlows");

        flows.MapGet("/{id:guid}", async (Guid id, FlowRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, null, ct) is { } flow ? (IResult)Results.Ok(flow) : ApiProblem.NotFound())
            .WithName("GetFlow");

        flows.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version, FlowRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, version, ct) is { } flow ? (IResult)Results.Ok(flow) : ApiProblem.NotFound())
            .WithName("GetFlowVersion");

        flows.MapPut("/{id:guid}", async (Guid id, SaveFlowRequest request, FlowDefinitionValidator validator, FlowRepository repository, CancellationToken ct) =>
        {
            var (_, validation) = validator.ValidateDocument(JsonSerializer.SerializeToElement(request.Definition, FlowRepository.JsonOptions));
            if (!validation.IsValid) return (IResult)Results.UnprocessableEntity(validation);
            if (await repository.SaveAsync(request, ct) is not { } saved)
                return ApiProblem.Conflict("Une autre version a été enregistrée. Rechargez le flow avant de publier vos modifications.");
            return Results.Ok(saved);
        }).AddEndpointFilter<ValidateSaveFlowFilter>().WithName("PublishFlowVersion");

        flows.MapGet("/components", (FlowComponentRegistry registry) => Results.Ok(registry.Plugins))
            .WithName("GetFlowComponentCatalog");

        flows.MapPost("/validate", (JsonElement document, FlowDefinitionValidator validator) =>
        {
            var (_, result) = validator.ValidateDocument(document);
            return result.IsValid ? (IResult)Results.Ok(result) : Results.UnprocessableEntity(result);
        }).AllowViewer().WithName("ValidateFlowDefinition");
    }
}
