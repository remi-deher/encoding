using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowScriptEndpoints
{
    public static void MapFlowScriptEndpoints(this WebApplication app)
    {
        var scripts = app.MapGroup("/api/v1/scripts").WithTags("Scripts");
        scripts.MapGet("", async (FlowScriptRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct))).WithName("ListFlowScripts");
        scripts.MapPost("", async (CreateFlowScriptRequest request, FlowScriptRepository repository, CancellationToken ct) =>
        {
            var errors = Validate(request.Draft, allowEmptyCode: true);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            try
            {
                var id = Guid.NewGuid();
                var script = await repository.CreateAsync(id, request.Draft, ct);
                return Results.Created($"/api/v1/scripts/{id:D}", script);
            }
            catch (DbException exception) when (Sql.IsUniqueViolation(exception))
            {
                return Results.Conflict(new { title = "Un script porte déjà ce nom." });
            }
        }).WithName("CreateFlowScript");
        scripts.MapGet("/{id:guid}", async (Guid id, FlowScriptRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } script ? Results.Ok(script) : Results.NotFound()).WithName("GetFlowScript");
        scripts.MapPut("/{id:guid}/draft", async (Guid id, SaveFlowScriptDraftRequest request, FlowScriptRepository repository, CancellationToken ct) =>
        {
            var errors = Validate(request.Draft, allowEmptyCode: true);
            if (request.ExpectedRevision < 1) errors["expectedRevision"] = ["Révision de brouillon invalide."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            try
            {
                var saved = await repository.SaveDraftAsync(id, request, ct);
                if (saved is not null) return Results.Ok(saved);
                return await repository.GetAsync(id, ct) is null
                    ? Results.NotFound()
                    : Results.Conflict(new { title = "Le brouillon a été modifié ailleurs. Rechargez-le avant de continuer." });
            }
            catch (DbException exception) when (Sql.IsUniqueViolation(exception))
            {
                return Results.Conflict(new { title = "Un script porte déjà ce nom." });
            }
        }).WithName("SaveFlowScriptDraft");
        scripts.MapPost("/{id:guid}/publish", async (Guid id, PublishFlowScriptRequest request, FlowScriptRepository repository, CancellationToken ct) =>
        {
            if (request.ExpectedRevision < 1) return Results.BadRequest(new { title = "Révision de brouillon invalide." });
            var current = await repository.GetAsync(id, ct);
            if (current is null) return Results.NotFound();
            if (current.Script.DraftRevision != request.ExpectedRevision)
                return Results.Conflict(new { title = "Le brouillon a été modifié ailleurs. Rechargez-le avant de publier." });
            if (string.IsNullOrWhiteSpace(current.Draft.Code))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Le script doit contenir du code avant sa publication."] });
            var published = await repository.PublishAsync(id, request.ExpectedRevision, ct);
            return published is null
                ? Results.Conflict(new { title = "Le brouillon a changé pendant la publication." })
                : Results.Created($"/api/v1/scripts/{id:D}/versions/{published.Version}", published);
        }).WithName("PublishFlowScript");
        scripts.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version, FlowScriptRepository repository, CancellationToken ct) =>
            version < 1 ? Results.BadRequest() : await repository.GetVersionAsync(id, version, ct) is { } published ? Results.Ok(published) : Results.NotFound())
            .WithName("GetFlowScriptVersion");
    }

    private static Dictionary<string, string[]> Validate(FlowScriptDraft? draft, bool allowEmptyCode)
    {
        var errors = new Dictionary<string, string[]>();
        if (draft is null) return new() { ["draft"] = ["Le brouillon est obligatoire."] };
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 128)
            errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (draft.Language is not ("javascript" or "python")) errors["language"] = ["Choisissez JavaScript ou Python."];
        if (draft.Description is null || draft.Description.Length > 1000) errors["description"] = ["La description est limitée à 1 000 caractères."];
        if (draft.Code is null || draft.Code.Length > 200000 || (!allowEmptyCode && string.IsNullOrWhiteSpace(draft.Code)))
            errors["code"] = ["Le code est obligatoire à la publication et limité à 200 000 caractères."];
        return errors;
    }
}
