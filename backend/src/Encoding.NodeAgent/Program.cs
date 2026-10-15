using Encoding.Core.Nodes;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.FirstOrDefault() == "--isolated-process")
{
    Environment.Exit(ProcessSandbox.Run(args));
    return;
}
var builder = WebApplication.CreateBuilder(args);
var options = NodeAgentOptions.FromConfiguration(builder.Configuration);
builder.WebHost.ConfigureKestrel(server => server.ListenAnyIP(options.LocalApiPort));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WorkerIdentityStore>();
builder.Services.AddSingleton<ProcessToolRunner>();
builder.Services.AddSingleton<PluginProcessHost>();
builder.Services.AddSingleton<MediaScanner>();
builder.Services.AddSingleton<MediaActions>();
builder.Services.AddSingleton<RuntimeFlowRunner>();
builder.Services.AddHostedService<RuntimeTransferService>();
builder.Services.AddHostedService<SourceReplacementService>();
builder.Services.AddHostedService<LibraryDiscoveryService>();
builder.Services.AddHostedService<RuntimeWorkerService>();
builder.Services.AddSingleton<IToolCapabilityProbe, ToolCapabilityProbe>();
builder.Services.AddSingleton<WorkerClusterDiscovery>();
builder.Services.AddSingleton<WorkerEnrollmentService>();
builder.Services.AddHostedService<WorkerClusterDiscovery>(provider => provider.GetRequiredService<WorkerClusterDiscovery>());
builder.Services.AddHostedService<WorkerAutoEnrollmentService>();
builder.Services.AddHostedService<NodeHeartbeatService>();
builder.Services.AddNodeModeControl(builder.Configuration[NodeModeEndpoints.ControlUrlSetting]);
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(options.WorkerUiOrigins.ToArray())
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();
var allowedOrigins = new HashSet<string>(options.WorkerUiOrigins, StringComparer.OrdinalIgnoreCase);
app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method)
        && (!context.Request.Headers.TryGetValue("Origin", out var origin) || !allowedOrigins.Contains(origin.ToString())))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseCors();

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapNodeMode("/api/v1/worker/mode");
app.MapGet("/api/v1/worker/clusters", (WorkerClusterDiscovery discovery) => Results.Ok(discovery.List()));
app.MapPost("/api/v1/worker/clusters", async (WorkerClusterAddress request, WorkerClusterDiscovery discovery, CancellationToken cancellationToken) =>
{
    try
    {
        return (IResult)Results.Ok(await discovery.AddByAddressAsync(request.ApiBaseUrl, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("AddWorkerClusterByAddress");
app.MapDelete("/api/v1/worker/clusters/{clusterId:guid}", (Guid clusterId, WorkerClusterDiscovery discovery) =>
    discovery.Forget(clusterId)
        ? Results.NoContent()
        : Results.NotFound(new { title = "Seule une adresse ajoutée à la main peut être oubliée ; ce cluster est détecté automatiquement." })).WithName("ForgetWorkerCluster");
app.MapGet("/api/v1/worker/status", async (NodeAgentOptions agentOptions, WorkerIdentityStore identityStore, IToolCapabilityProbe capabilityProbe, CancellationToken cancellationToken) =>
{
    var identity = await identityStore.GetAsync(cancellationToken);
    var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
    return Results.Ok(new
    {
        nodeId = identity.Id,
        name = agentOptions.Name,
        roles = agentOptions.Roles,
        maxConcurrentTasks = agentOptions.MaxConcurrentTasks,
        automaticEnrollmentEnabled = agentOptions.AutomaticEnrollmentEnabled,
        capabilities,
        identity.IsEnrolled,
        pairingKey = identity.IsPairingPending && identity.PairingExpiresAt > DateTimeOffset.UtcNow ? identity.PairingKey : null,
        pairingExpiresAt = identity.IsPairingPending ? identity.PairingExpiresAt : null,
        clusterId = identity.ClusterId,
        clusterName = identity.ClusterName,
        verificationCode = identity.HasCredentials ? NodeVerificationCode.FromToken(identity.Id, identity.NodeToken!) : null,
        switching = identity.IsSwitching
            ? new { previousClusterName = identity.Previous!.ClusterName, deadline = identity.SwitchDeadline }
            : null,
        switchFailure = identity.SwitchFailure
    });
});
app.MapPost("/api/v1/worker/enroll", async (
    WorkerEnrollmentRequest request,
    WorkerEnrollmentService enrollment,
    CancellationToken cancellationToken) =>
{
    try
    {
        return (IResult)Results.Ok(await enrollment.EnrollAsync(request.ClusterId, cancellationToken, request.LeaveCurrent));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("ConfirmWorkerEnrollment");

app.MapPost("/api/v1/worker/pairing-key", async (
    WorkerEnrollmentRequest request,
    WorkerEnrollmentService enrollment,
    CancellationToken cancellationToken) =>
{
    try
    {
        return (IResult)Results.Ok(await enrollment.CreatePairingKeyAsync(request.ClusterId, cancellationToken, request.LeaveCurrent));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("CreateWorkerPairingKey");

app.MapPost("/api/v1/worker/leave", async (WorkerEnrollmentService enrollment, CancellationToken cancellationToken) =>
{
    try
    {
        await enrollment.LeaveAsync(cancellationToken);
        return (IResult)Results.NoContent();
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("LeaveWorkerCluster");

await app.RunAsync();

public sealed record WorkerClusterAddress(string ApiBaseUrl);

public sealed record WorkerEnrollmentRequest(Guid ClusterId, bool LeaveCurrent = false);
