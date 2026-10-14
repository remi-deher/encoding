using Encoding.Contracts.Flows;
using Npgsql;

namespace Encoding.Api.Flows;

public static class FlowTemplateEndpoints
{
    public static void MapFlowTemplateEndpoints(this WebApplication app)
    {
        var templates = app.MapGroup("/api/v1/templates").WithTags("Modèles");
        templates.MapGet("", async (FlowTemplateRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct))).WithName("ListFlowTemplates");
        templates.MapGet("/{id:guid}", async (Guid id, FlowTemplateRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } template ? Results.Ok(template) : Results.NotFound())
            .WithName("GetFlowTemplate");
        templates.MapPost("/from-flow", async (
            CreateFlowTemplateFromFlowRequest request,
            FlowTemplateRepository repository,
            CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            try
            {
                var id = Guid.NewGuid();
                var template = await repository.CreateFromFlowAsync(id, request, ct);
                return template is null
                    ? Results.NotFound(new { title = "Cette version publiée du flow est introuvable." })
                    : Results.Created($"/api/v1/templates/{id:D}", template);
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                return Results.Conflict(new { title = "Un modèle porte déjà ce nom." });
            }
        }).WithName("CreateFlowTemplateFromFlow");
    }

    private static Dictionary<string, string[]> Validate(CreateFlowTemplateFromFlowRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
            return new() { ["request"] = ["La demande est obligatoire."] };
        if (request.SourceFlowId == Guid.Empty)
            errors["sourceFlowId"] = ["Choisissez un flow publié."];
        if (request.SourceVersion < 1)
            errors["sourceVersion"] = ["La version publiée doit être positive."];
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            errors["name"] = ["Le nom du modèle est obligatoire et limité à 128 caractères."];
        if (request.Description is null || request.Description.Length > 1000)
            errors["description"] = ["La description est limitée à 1 000 caractères."];
        return errors;
    }
}
