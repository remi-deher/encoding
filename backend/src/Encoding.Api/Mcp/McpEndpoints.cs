using System.Security.Claims;
using System.Text.Json;
using Encoding.Api.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Encoding.Api.Mcp;

public static class McpEndpoints
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapMcpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/mcp").WithTags("MCP");

        // Transport HTTP/SSE : Connexion initiale du client MCP
        group.MapGet("/sse", async (
            HttpContext http,
            McpSessionManager sessionManager,
            CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var sessionId = Guid.NewGuid().ToString("N");
            var reader = sessionManager.CreateSession(sessionId);

            try
            {
                await http.Response.WriteAsync($"event: endpoint\ndata: /api/v1/mcp/message?sessionId={sessionId}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);

                while (!ct.IsCancellationRequested && await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var response))
                    {
                        var json = JsonSerializer.Serialize(response, JsonOptions);
                        await http.Response.WriteAsync($"event: message\ndata: {json}\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                sessionManager.CloseSession(sessionId);
            }
        }).AllowViewer().WithName("McpSse");

        // Transport HTTP/SSE : Envoi d'un message JSON-RPC vers la session active
        group.MapPost("/message", async (
            [FromQuery] string sessionId,
            [FromBody] McpJsonRpcRequest request,
            McpService mcpService,
            McpSessionManager sessionManager,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest("Paramètre 'sessionId' manquant.");

            var response = await mcpService.HandleMessageAsync(request, user, ct);
            if (response is not null)
            {
                var delivered = await sessionManager.SendMessageAsync(sessionId, response, ct);
                if (!delivered)
                    return Results.NotFound($"Session MCP '{sessionId}' expirée ou introuvable.");
            }

            return Results.Accepted();
        }).AllowViewer().WithName("McpMessage");

        // Transport HTTP direct (sans SSE) : Requête/Réponse JSON-RPC unitaire
        group.MapPost("", async (
            [FromBody] McpJsonRpcRequest request,
            McpService mcpService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var response = await mcpService.HandleMessageAsync(request, user, ct);
            return response is not null ? Results.Ok(response) : Results.NoContent();
        }).AllowViewer().WithName("McpDirect");
    }
}
