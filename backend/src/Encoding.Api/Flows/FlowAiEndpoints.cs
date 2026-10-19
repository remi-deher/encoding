using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Encoding.Contracts.Flows;
using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using System.Security.Claims;

namespace Encoding.Api.Flows;

public static class FlowAiEndpoints
{
    public static void MapFlowAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/flows/ai").WithTags("Flow AI Assistant");

        group.MapPost("/generate", async (FlowAiGenerateRequest request, FlowAiGeneratorService service, AiAssistantService activity, AiClusterService cluster, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                return Results.BadRequest(new { message = "Le prompt utilisateur est obligatoire." });
            }
            var requestId = request.RequestId ?? Guid.NewGuid();
            var provider = request.Provider ?? (await cluster.GetSettingsAsync(ct)).DefaultProvider;
            request = request with { Provider = provider };
            var startedAt = DateTimeOffset.UtcNow;
            await activity.RecordOperationActivityAsync(user, requestId, "flow-generation", provider, "running", startedAt, null, ct);
            try
            {
                var response = await service.GenerateFlowAsync(request with { RequestId = requestId }, ct);
                await activity.RecordOperationActivityAsync(user, requestId, "flow-generation", provider, response.Success ? "succeeded" : "failed", startedAt,
                    response.Success ? null : "invalid-result", ct);
                return Results.Ok(response);
            }
            catch
            {
                await activity.RecordOperationActivityAsync(user, requestId, "flow-generation", provider, "failed", startedAt, "provider-error", CancellationToken.None);
                throw;
            }
        }).WithName("GenerateFlowWithAi");

        group.MapPost("/refactor", async (FlowAiRefactorRequest request, FlowAiGeneratorService service, AiAssistantService activity, AiClusterService cluster, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (request.CurrentFlow is null || string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                return Results.BadRequest(new { message = "Le flow courant et le prompt sont obligatoires." });
            }
            var requestId = request.RequestId ?? Guid.NewGuid();
            var provider = request.Provider ?? (await cluster.GetSettingsAsync(ct)).DefaultProvider;
            request = request with { Provider = provider };
            var startedAt = DateTimeOffset.UtcNow;
            await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, "running", startedAt, null, ct);
            try
            {
                var response = await service.RefactorFlowAsync(request with { RequestId = requestId }, ct);
                await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, response.Success ? "succeeded" : "failed", startedAt,
                    response.Success ? null : "invalid-result", ct);
                return Results.Ok(response);
            }
            catch
            {
                await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, "failed", startedAt, "provider-error", CancellationToken.None);
                throw;
            }
        }).WithName("RefactorFlowWithAi");

        group.MapPost("/explain", async (FlowAiExplainRequest request, FlowAiGeneratorService service, AiAssistantService activity, AiClusterService cluster, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (request.Flow is null)
            {
                return Results.BadRequest(new { message = "Le flow à expliquer est obligatoire." });
            }
            var requestId = request.RequestId ?? Guid.NewGuid();
            var provider = request.Provider ?? (await cluster.GetSettingsAsync(ct)).DefaultProvider;
            request = request with { Provider = provider };
            var startedAt = DateTimeOffset.UtcNow;
            await activity.RecordOperationActivityAsync(user, requestId, "flow-explain", provider, "running", startedAt, null, ct);
            try
            {
                var response = await service.ExplainFlowAsync(request with { RequestId = requestId }, ct);
                await activity.RecordOperationActivityAsync(user, requestId, "flow-explain", provider, response.Success ? "succeeded" : "failed", startedAt,
                    response.Success ? null : "provider-error", ct);
                return Results.Ok(response);
            }
            catch
            {
                await activity.RecordOperationActivityAsync(user, requestId, "flow-explain", provider, "failed", startedAt, "provider-error", CancellationToken.None);
                throw;
            }
        }).WithName("ExplainFlowWithAi");
    }
}
