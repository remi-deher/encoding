using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public static class AiToolEndpoints
{
    public static void MapAiToolEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tools/ai").WithTags("AI Tools");

        group.MapGet("/", (DeviceCodeAuthService service) =>
            Results.Ok(service.ListTools()))
            .WithName("ListAiTools");

        group.MapGet("/{id}/status", (string id, DeviceCodeAuthService service) =>
            Results.Ok(service.GetStatus(id)))
            .WithName("GetAiToolStatus");

        group.MapPost("/{id}/auth/device-code/start", (string id, DeviceCodeAuthService service) =>
        {
            var res = service.StartDeviceAuth(id);
            return Results.Ok(res);
        }).WithName("StartAiToolDeviceAuth");

        group.MapGet("/{id}/auth/device-code/status", (string id, DeviceCodeAuthService service) =>
            Results.Ok(service.GetStatus(id)))
            .WithName("GetAiToolDeviceAuthStatus");

        group.MapPost("/{id}/auth/complete", (string id, DeviceCodeAuthService service, HttpContext http) =>
        {
            var success = service.CompleteAuthorization(id);
            return Results.Ok(new { success });
        }).WithName("CompleteAiToolAuth");

        group.MapDelete("/{id}/auth", (string id, DeviceCodeAuthService service) =>
        {
            var success = service.RevokeAuth(id);
            return Results.Ok(new { revoked = success });
        }).WithName("RevokeAiToolAuth");
    }
}
