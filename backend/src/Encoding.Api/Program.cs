using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Infrastructure;
using Encoding.Api.Nodes;
using Encoding.Api.Libraries;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Libraries;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Cluster")
    ?? throw new InvalidOperationException("La chaîne de connexion 'Cluster' est obligatoire.");
var offlineAfterSeconds = builder.Configuration.GetValue("NodePresence:OfflineAfterSeconds", 30);
if (offlineAfterSeconds <= 0)
{
    throw new InvalidOperationException("NodePresence:OfflineAfterSeconds doit être supérieur à zéro.");
}
var nodeApiToken = builder.Configuration["Cluster:NodeApiToken"]
    ?? throw new InvalidOperationException("Le jeton 'Cluster:NodeApiToken' est obligatoire.");
if (string.IsNullOrWhiteSpace(nodeApiToken))
{
    throw new InvalidOperationException("Le jeton 'Cluster:NodeApiToken' ne peut pas être vide.");
}

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(new NodePresenceOptions(TimeSpan.FromSeconds(offlineAfterSeconds)));
builder.Services.AddSingleton<NodeRepository>();
builder.Services.AddSingleton<LibraryRepository>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
await DatabaseMigrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Environment.ContentRootPath, app.Lifetime.ApplicationStopping);

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }))
    .WithTags("Health");
app.MapGet("/health/ready", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
        return (IResult)Results.Ok(new { status = "ready" });
    }
    catch (NpgsqlException)
    {
        return (IResult)Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Base de données indisponible.");
    }
}).WithTags("Health");

var libraries = app.MapGroup("/api/v1/libraries").WithTags("Libraries");
libraries.MapGet("", async (LibraryRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListAsync(cancellationToken)))
    .WithName("ListLibraries");
libraries.MapPost("", async (CreateLibraryRequest request, LibraryRepository repository, CancellationToken cancellationToken) =>
{
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Name)) errors["name"] = ["Le nom de la bibliothèque est obligatoire."];
    else if (request.Name.Trim().Length > 128) errors["name"] = ["Le nom ne peut pas dépasser 128 caractères."];
    if (!Enum.IsDefined(request.MediaType)) errors["mediaType"] = ["Le type de média n’est pas pris en charge."];
    if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
    var created = await repository.CreateAsync(request, cancellationToken);
    return created is null
        ? Results.Conflict(new { title = "Une bibliothèque porte déjà ce nom." })
        : Results.Created($"/api/v1/libraries/{created.Id}", created);
}).WithName("CreateLibrary");

var nodes = app.MapGroup("/api/v1/nodes").WithTags("Nodes");
nodes.MapPut("/{nodeId:guid}/heartbeat", async (
    Guid nodeId,
    NodeHeartbeatRequest request,
    NodeRepository repository,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var providedToken = httpContext.Request.Headers["X-Node-Token"].ToString();
    if (!NodeTokenAuthentication.Matches(providedToken, nodeApiToken))
    {
        return (IResult)Results.Unauthorized();
    }

    var errors = NodeHeartbeatValidator.Validate(request);
    if (errors.Count > 0)
    {
        return (IResult)Results.ValidationProblem(errors);
    }

    var node = await repository.RecordHeartbeatAsync(nodeId, request, cancellationToken);
    return (IResult)Results.Ok(node);
}).WithName("RecordNodeHeartbeat");

nodes.MapGet("", async (NodeRepository repository, NodePresenceOptions options, CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListAsync(options.OfflineAfter, cancellationToken)))
    .WithName("ListNodes");

nodes.MapGet("/{nodeId:guid}", async (Guid nodeId, NodeRepository repository, NodePresenceOptions options, CancellationToken cancellationToken) =>
{
    var node = await repository.GetAsync(nodeId, options.OfflineAfter, cancellationToken);
    return node is null ? (IResult)Results.NotFound() : Results.Ok(node);
}).WithName("GetNode");

app.Run();

public partial class Program { }

static class NodeTokenAuthentication
{
    public static bool Matches(string suppliedToken, string configuredToken)
    {
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(suppliedToken);
        var configuredBytes = System.Text.Encoding.UTF8.GetBytes(configuredToken);
        return suppliedBytes.Length == configuredBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }
}
