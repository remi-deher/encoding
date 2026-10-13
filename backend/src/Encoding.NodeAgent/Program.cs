using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var options = NodeAgentOptions.FromConfiguration(builder.Configuration);
builder.WebHost.ConfigureKestrel(server => server.ListenAnyIP(options.LocalApiPort));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WorkerIdentityStore>();
builder.Services.AddSingleton<IToolCapabilityProbe, ToolCapabilityProbe>();
builder.Services.AddSingleton<WorkerClusterDiscovery>();
builder.Services.AddSingleton<WorkerEnrollmentService>();
builder.Services.AddHostedService<WorkerClusterDiscovery>(provider => provider.GetRequiredService<WorkerClusterDiscovery>());
builder.Services.AddHostedService<WorkerAutoEnrollmentService>();
builder.Services.AddHostedService<NodeHeartbeatService>();
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
    if (HttpMethods.IsPost(context.Request.Method)
        && (!context.Request.Headers.TryGetValue("Origin", out var origin) || !allowedOrigins.Contains(origin.ToString())))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseCors();

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/api/v1/worker/clusters", (WorkerClusterDiscovery discovery) => Results.Ok(discovery.List()));
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
        clusterName = identity.ClusterName
    });
});
app.MapPost("/api/v1/worker/enroll", async (
    WorkerEnrollmentRequest request,
    WorkerEnrollmentService enrollment,
    CancellationToken cancellationToken) =>
{
    try
    {
        return (IResult)Results.Ok(await enrollment.EnrollAsync(request.ClusterId, cancellationToken));
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
        return (IResult)Results.Ok(await enrollment.CreatePairingKeyAsync(request.ClusterId, cancellationToken));
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

await app.RunAsync();

public sealed record WorkerEnrollmentRequest(Guid ClusterId);
