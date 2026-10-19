using Encoding.Api.Auth;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public static class AiToolEndpoints
{
    public static void MapAiToolEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tools/ai").WithTags("AI Tools");
        group.MapGet("/", async (DeviceCodeAuthService service, CancellationToken ct) => Results.Ok(await service.ListToolsAsync(ct)));
        group.MapGet("/cluster", async (AiClusterService service, CancellationToken ct) => Results.Ok(await service.ViewAsync(ct)));
        group.MapGet("/quotas", async (AiClusterService service, CancellationToken ct) =>
            Results.Ok(await service.QuotasAsync(ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/{id}/status", async (string id, DeviceCodeAuthService service, CancellationToken ct) => await GuardAsync(async () => Results.Ok(await service.GetStatusAsync(id, ct))));
        group.MapPost("/{id}/auth/device-code/start", async (string id, DeviceCodeAuthService service, AiQuotaService quotas, CancellationToken ct) =>
            await GuardAsync(async () => { quotas.Invalidate(id); return Results.Ok(await service.StartDeviceAuthAsync(id, ct)); }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPost("/{id}/auth/input", async (string id, AiCliAuthInputRequest request, DeviceCodeAuthService service, CancellationToken ct) =>
            await GuardAsync(async () => { await service.SubmitAuthInputAsync(id, request.Input, ct); return Results.Ok(new { accepted = true }); }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/{id}/auth/device-code/status", async (string id, DeviceCodeAuthService service, CancellationToken ct) => await GuardAsync(async () => Results.Ok(await service.GetStatusAsync(id, ct))));
        group.MapPost("/claude-cli/auth/token", async (ClaudeTokenRequest request, DeviceCodeAuthService service, AiQuotaService quotas, CancellationToken ct) =>
            await GuardAsync(async () => { quotas.Invalidate("claude-cli"); try { await service.SaveClaudeTokenAsync(request.Token, ct); return Results.Ok(new { saved = true }); } finally { quotas.Invalidate("claude-cli"); } }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPost("/{id}/auth/cancel", (string id, DeviceCodeAuthService service) =>
        {
            service.Cancel(id);
            return Results.Ok();
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapDelete("/{id}/auth", async (string id, DeviceCodeAuthService service, AiQuotaService quotas, CancellationToken ct) =>
            await GuardAsync(async () => { quotas.Invalidate(id); try { return Results.Ok(new { revoked = await service.RevokeAuthAsync(id, ct) }); } finally { quotas.Invalidate(id); } }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/settings", async (AiClusterService service, CancellationToken ct) => PublicSettings(await service.GetSettingsAsync(ct)));
        group.MapPut("/settings", async (AiSettingsUpdateRequest request, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(async () => PublicSettings(await service.UpdateSettingsAsync(request, ct))))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/sessions", async (int? offset, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.ListSessionsAsync(offset ?? 0, ct))))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/sessions/{id}", async (string id, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(async () => await service.GetSessionAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound()))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPost("/sessions/{id}/fork", async (string id, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.ForkSessionAsync(id, ct))))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPost("/generate", async (AiGenerateRequest request, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.GenerateAsync(request.Provider, request.Prompt, ct))))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy).RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);
        // Signed peer admission + AES-GCM payload are checked inside ReceiveAsync, independently of user sessions.
        app.MapPost(AiClusterTransport.Path, async (HttpContext http, AiClusterTransport transport, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(() => transport.ReceiveAsync(http, service, ct)))
            .AllowAnonymous().RequireRateLimiting(AuthExtensions.ClusterMeshPolicy);
    }
    private static IResult PublicSettings(AiSettingsDto settings) => Results.Ok(new
    {
        settings.DefaultProvider, settings.OllamaBaseUrl, settings.OllamaModel, settings.OpenAiBaseUrl,
        settings.OpenAiModel, HasOpenAiApiKey = !string.IsNullOrWhiteSpace(settings.OpenAiApiKey), settings.TimeoutSeconds, settings.SubtitleBatchSize
    });
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { title = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { title = ex.Message }); }
        catch (System.Security.Cryptography.CryptographicException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
        catch (System.Text.Json.JsonException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
        catch (FormatException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
    }
}
