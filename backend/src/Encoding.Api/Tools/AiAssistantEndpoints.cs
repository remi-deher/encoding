using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Contracts.Tools;
using Encoding.Api.Mcp;

namespace Encoding.Api.Tools;

public static class AiAssistantEndpoints
{
    public static void MapAiAssistantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tools/ai/assistant").WithTags("AI Assistant");

        group.MapGet("/conversations", async (HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.ListAsync(http.User, ct))));

        group.MapGet("/activity", async (HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.ActivityAsync(http.User, ct))));

        group.MapGet("/conversations/{id}", async (string id, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var detail = await service.GetAsync(id, http.User, ct);
                return detail is null ? Results.NotFound() : Results.Ok(detail);
            }));

        group.MapPut("/conversations/{id}/sources", async (string id, AiAssistantSourcesRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var conversation = await service.UpdateSourcesAsync(id, request, http.User, ct);
                return conversation is null ? Results.NotFound() : Results.Ok(conversation);
            }))
        .AllowAssistantConversation();

        group.MapPost("/messages", async (AiAssistantMessageRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var result = await service.SendAsync(request, http.User, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }))
        .AllowAssistantConversation()
        .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);

        group.MapPost("/requests/{requestId:guid}/cancel", (Guid requestId, HttpContext http, AiAssistantService service) =>
            service.CancelRequest(requestId, http.User)
                ? Results.Accepted(value: new { requestId, state = "cancellation-requested" })
                : Results.Conflict(new { title = "Aucune demande active ne correspond à cet identifiant." }))
        .AllowAssistantConversation();

        group.MapPost("/tools/call", async (AiAssistantToolCall call, McpService mcp, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await mcp.CallAssistantReadToolAsync(call, ct))))
        .AllowAssistantConversation()
        .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);

        group.MapPost("/activity/{requestId:guid}/proposal", async (Guid requestId, AiAssistantProposalDecision request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => await service.UpdateFlowProposalActivityAsync(requestId, request.Decision, http.User, ct)
                ? Results.Ok(new { requestId, status = request.Decision })
                : Results.Conflict(new { title = "Cette proposition n’est plus en attente de décision." })))
        .AllowAssistantConversation();
    }

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { title = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { title = ex.Message }); }
        catch (CryptographicException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
        catch (JsonException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
        catch (FormatException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
    }
}
