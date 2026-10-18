using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowAiEndpoints
{
    public static void MapFlowAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/flows/ai").WithTags("Flow AI Assistant");

        group.MapPost("/generate", async (FlowAiGenerateRequest request, FlowAiGeneratorService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                return Results.BadRequest(new { message = "Le prompt utilisateur est obligatoire." });
            }
            var response = await service.GenerateFlowAsync(request, ct);
            return Results.Ok(response);
        }).WithName("GenerateFlowWithAi");

        group.MapPost("/refactor", async (FlowAiRefactorRequest request, FlowAiGeneratorService service, CancellationToken ct) =>
        {
            if (request.CurrentFlow is null || string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                return Results.BadRequest(new { message = "Le flow courant et le prompt sont obligatoires." });
            }
            var response = await service.RefactorFlowAsync(request, ct);
            return Results.Ok(response);
        }).WithName("RefactorFlowWithAi");

        group.MapPost("/explain", async (FlowAiExplainRequest request, FlowAiGeneratorService service, CancellationToken ct) =>
        {
            if (request.Flow is null)
            {
                return Results.BadRequest(new { message = "Le flow à expliquer est obligatoire." });
            }
            var response = await service.ExplainFlowAsync(request, ct);
            return Results.Ok(response);
        }).WithName("ExplainFlowWithAi");
    }
}
