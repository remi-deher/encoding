using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowEndpoints
{
    public static void MapFlowEndpoints(this WebApplication app)
    {
        var flows = app.MapGroup("/api/v1/flows").WithTags("Flows");
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
            if (!await repository.SaveAsync(request,ct)) return Results.Conflict(new { title="Une autre version a été enregistrée. Rechargez le flow avant de publier vos modifications." });
            return Results.Ok(await repository.GetAsync(id,request.Definition.Version,ct));
        });
        flows.MapGet("/components", (FlowComponentRegistry registry) => Results.Ok(registry.Plugins))
            .WithName("GetFlowComponentCatalog");
        flows.MapPost("/validate", (JsonElement document, FlowDefinitionValidator validator) =>
        {
            var (_, result) = validator.ValidateDocument(document);
            return result.IsValid ? (IResult)Results.Ok(result) : Results.UnprocessableEntity(result);
        }).WithName("ValidateFlowDefinition");
    }
}
