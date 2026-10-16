using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Encoding.Api.Auth;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerProtocolStatus(int Protocol, string Schema, long Revision, bool Ready, Guid PeerId, string Thumbprint,
    IReadOnlyList<string>? Members = null);
public sealed record PeerForwardedUser(string Id, string Name, string Role);

public static class PeerRequestRouting
{
    public const int Protocol = 1;
    public const string ProtocolHeader = "X-Encoding-Peer-Protocol";
    public const string SchemaHeader = "X-Encoding-Peer-Schema";
    public const string PrincipalHeader = "X-Encoding-Peer-Principal";
    public const string PrincipalItem = "Encoding.Peer.Principal";
    public const string StatusPath = "/cluster-control/status";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Authorization", "Cookie", "Set-Cookie", PrincipalHeader, ProtocolHeader, SchemaHeader };

    public static bool IsClusterRoute(PathString path) => path.StartsWithSegments("/api/v1")
        && !path.StartsWithSegments("/api/v1/auth") && !path.StartsWithSegments("/api/v1/local-node")
        && !path.StartsWithSegments("/api/v1/peer-cluster") && !path.StartsWithSegments("/api/v1/node-mode");

    public static void AddProtocolHeaders(HttpRequestMessage request, PeerDatabaseCoordinator database)
    {
        request.Headers.TryAddWithoutValidation(ProtocolHeader, Protocol.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(SchemaHeader, database.Schema.Fingerprint);
    }

    public static bool Compatible(HttpContext context, PeerDatabaseCoordinator database) =>
        context.Request.Headers[ProtocolHeader] == Protocol.ToString(System.Globalization.CultureInfo.InvariantCulture)
        && context.Request.Headers[SchemaHeader] == database.Schema.Fingerprint;

    public static PeerProtocolStatus Status(PeerDatabaseCoordinator database, PeerIdentityStore identity, PeerConsensusService? consensus = null) =>
        new(Protocol, database.Schema.Fingerprint, database.Image?.Revision ?? 0, database.Image is not null && !database.NeedsRepair,
            identity.Identity.PeerId, identity.Identity.Thumbprint, consensus?.GetStatus().Members);

    public static async Task<bool> CheckPeerAsync(Uri endpoint, IServiceProvider services, CancellationToken ct)
    {
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("raftClient");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(new Uri(endpoint, StatusPath), timeout.Token);
        if (!response.IsSuccessStatusCode) return false;
        var status = await response.Content.ReadFromJsonAsync<PeerProtocolStatus>(Json, timeout.Token);
        var database = services.GetRequiredService<PeerDatabaseCoordinator>();
        var members = services.GetRequiredService<PeerConsensusService>().GetStatus().Members;
        return status is { Protocol: Protocol } && status.Schema == database.Schema.Fingerprint
            && (!status.Ready || status.Members?.Any(member => members.Contains(member, StringComparer.OrdinalIgnoreCase)) == true);
    }

    public static void UsePeerRequestRouting(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<PeerClusterOptions>().Enabled) return;
        app.Use(async (context, next) =>
        {
            try
            {
                var consensus = context.RequestServices.GetRequiredService<PeerConsensusService>();
                var safe = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
                    || HttpMethods.IsOptions(context.Request.Method);
                if (!safe && IsClusterRoute(context.Request.Path) && !consensus.GetStatus().IsLeader)
                {
                    if (context.Items.ContainsKey(PrincipalItem)
                        || consensus.GetStatus().LeaderEndpoint is not { } leader)
                        throw new PeerWriteUnavailableException("Aucun leader disponible pour cette opération.");
                    await ForwardAsync(context, new Uri(leader));
                    return;
                }
                await next(context);
            }
            catch (Exception exception) when ((exception is PeerWriteUnavailableException or TimeoutException or HttpRequestException
                || exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
                && !context.Response.HasStarted)
            {
                app.Logger.LogWarning(exception, "Écriture répliquée momentanément indisponible pour {Path}.", context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { title = "L’opération n’a pas pu être confirmée par le quorum. Réessayez après rétablissement du cluster." });
            }
        });
    }

    private static async Task ForwardAsync(HttpContext context, Uri leader)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("raftClient");
        var target = new Uri(leader, context.Request.Path + context.Request.QueryString);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(context.Request.Body);
        foreach (var header in context.Request.Headers)
        {
            if (HopHeaders.Contains(header.Key)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var assertion = new PeerForwardedUser(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                context.User.Identity.Name ?? "", context.User.IsInRole("admin") ? "admin" : "viewer");
            request.Headers.TryAddWithoutValidation(PrincipalHeader,
                Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(assertion, Json)));
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (!HopHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
        context.Response.Headers.Remove("transfer-encoding");
        await response.Content.CopyToAsync(context.Response.Body, timeout.Token);
    }

    public static bool ReadPrincipal(HttpContext context)
    {
        if (context.Request.Headers[PrincipalHeader].ToString() is not { Length: > 0 and < 8192 } header)
        {
            context.Items[PrincipalItem] = false; // Also marks forwarded worker/webhook requests to prevent loops.
            return true;
        }
        try
        {
            var user = JsonSerializer.Deserialize<PeerForwardedUser>(Convert.FromBase64String(header), Json);
            if (user is null || !Guid.TryParse(user.Id, out _) || user.Role is not ("admin" or "viewer")) return false;
            context.Items[PrincipalItem] = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Role, user.Role)],
                SessionAuthentication.Scheme));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException) { return false; }
    }
}

public sealed class PeerProtocolHandler(PeerDatabaseCoordinator database) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        PeerRequestRouting.AddProtocolHeaders(request, database);
        return base.SendAsync(request, ct);
    }
}
