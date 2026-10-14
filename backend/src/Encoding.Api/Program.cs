using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Infrastructure;
using Encoding.Api.Nodes;
using Encoding.Api.Libraries;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Libraries;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Cluster")
    ?? throw new InvalidOperationException("La chaîne de connexion 'Cluster' est obligatoire.");
var offlineAfterSeconds = builder.Configuration.GetValue("NodePresence:OfflineAfterSeconds", 30);
if (offlineAfterSeconds <= 0)
{
    throw new InvalidOperationException("NodePresence:OfflineAfterSeconds doit être supérieur à zéro.");
}
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(new NodePresenceOptions(TimeSpan.FromSeconds(offlineAfterSeconds)));
builder.Services.AddSingleton<NodeRepository>();
builder.Services.AddSingleton<ExecutionRepository>();
builder.Services.AddSingleton<FlowComponentRegistry>();
builder.Services.AddSingleton<FlowDefinitionValidator>();
builder.Services.AddSingleton<FlowRepository>();
builder.Services.AddSingleton<FlowArchiveService>();
builder.Services.AddSingleton<RuntimeEligibility>();
builder.Services.AddSingleton<RuntimeRepository>();
builder.Services.AddSingleton<RuntimeArtifactStore>();
builder.Services.AddSingleton<RuntimeTransferRepository>();
builder.Services.AddHostedService<RuntimeRecoveryService>();
builder.Services.AddHostedService<FlowBackupService>();
builder.Services.AddSingleton<LibraryRepository>();
builder.Services.AddSingleton<LibraryNodeAccessRepository>();
builder.Services.AddSingleton<ClusterIdentityRepository>();
builder.Services.AddHostedService<ClusterDiscoveryPublisher>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
// Fail startup if the bundled installed catalog is invalid or missing.
app.Services.GetRequiredService<FlowComponentRegistry>();
await DatabaseMigrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Environment.ContentRootPath, app.Lifetime.ApplicationStopping);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFlowEndpoints();
app.MapRuntimeEndpoints();

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
    if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
    var created = await repository.CreateAsync(request, cancellationToken);
    return created is null
        ? Results.Conflict(new { title = "Une bibliothèque porte déjà ce nom." })
        : Results.Created($"/api/v1/libraries/{created.Id}", created);
}).WithName("CreateLibrary");

libraries.MapPut("/{libraryId:guid}", async (
    Guid libraryId,
    UpdateLibraryRequest request,
    LibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Name)) errors["name"] = ["Le nom de la bibliothèque est obligatoire."];
    else if (request.Name.Trim().Length > 128) errors["name"] = ["Le nom ne peut pas dépasser 128 caractères."];
    if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
    try
    {
        var updated = await repository.UpdateAsync(libraryId, request, cancellationToken);
        return updated is null ? (IResult)Results.NotFound() : Results.Ok(updated);
    }
    catch (PostgresException exception) when (exception.SqlState == "23505")
    {
        return Results.Conflict(new { title = "Une bibliothèque porte déjà ce nom." });
    }
}).WithName("UpdateLibrary");

libraries.MapPut("/{libraryId:guid}/activation", async (
    Guid libraryId,
    SetLibraryActivationRequest request,
    LibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var updated = await repository.SetActiveAsync(libraryId, request.IsActive, cancellationToken);
    return updated is null ? (IResult)Results.NotFound() : Results.Ok(updated);
}).WithName("SetLibraryActivation");

libraries.MapDelete("/{libraryId:guid}", async (
    Guid libraryId,
    LibraryRepository repository,
    CancellationToken cancellationToken) =>
    await repository.DeleteAsync(libraryId, cancellationToken) ? Results.NoContent() : Results.NotFound())
    .WithName("DeleteLibrary");

libraries.MapGet("/{libraryId:guid}/node-access", async (
    Guid libraryId,
    LibraryNodeAccessRepository repository,
    CancellationToken cancellationToken) =>
{
    var accesses = await repository.ListAsync(libraryId, cancellationToken);
    return accesses is null ? (IResult)Results.NotFound() : Results.Ok(accesses);
}).WithName("ListLibraryNodeAccesses");

libraries.MapPut("/{libraryId:guid}/node-access", async (
    Guid libraryId,
    ConfigureLibraryNodeAccessRequest request,
    LibraryNodeAccessRepository repository,
    CancellationToken cancellationToken) =>
{
    try
    {
        var accesses = await repository.ConfigureAsync(libraryId, request, cancellationToken);
        return accesses is null ? (IResult)Results.NotFound() : Results.Ok(accesses);
    }
    catch (LibraryNodeAccessValidationException exception)
    {
        return Results.ValidationProblem(exception.Errors.ToDictionary(pair => pair.Key, pair => pair.Value));
    }
}).WithName("ConfigureLibraryNodeAccesses");

libraries.MapPut("/{libraryId:guid}/node-access/{nodeId:guid}/activation", async (
    Guid libraryId,
    Guid nodeId,
    SetLibraryNodeAccessActivationRequest request,
    LibraryNodeAccessRepository repository,
    CancellationToken cancellationToken) =>
{
    var access = await repository.SetEnabledAsync(libraryId, nodeId, request.IsEnabled, cancellationToken);
    return access is null ? (IResult)Results.NotFound() : Results.Ok(access);
}).WithName("SetLibraryNodeAccessActivation");

var nodes = app.MapGroup("/api/v1/nodes").WithTags("Nodes");
app.MapGet("/api/v1/cluster", async (ClusterIdentityRepository repository, CancellationToken cancellationToken) =>
{
    var identity = await repository.GetAsync(cancellationToken);
    return Results.Ok(new { identity.Id, identity.Name });
}).WithTags("Cluster").WithName("GetClusterIdentity");

nodes.MapPost("/enroll", async (
    NodeEnrollmentRequest request,
    NodeRepository repository,
    CancellationToken cancellationToken) =>
{
    var errors = NodeHeartbeatValidator.Validate(request);
    if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
    var node = await repository.EnrollAsync(request, cancellationToken);
    return node is null
        ? Results.Conflict(new { title = "Cet identifiant appartient déjà à un autre nœud." })
        : Results.Ok(node);
}).WithName("EnrollNode");

nodes.MapPost("/enrollment-requests", async (
    CreateNodeEnrollmentRequest request,
    NodeRepository repository,
    CancellationToken cancellationToken) =>
{
    var errors = NodeHeartbeatValidator.Validate(request);
    if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
    var created = await repository.CreatePairingRequestAsync(request, cancellationToken);
    return created
        ? Results.Accepted()
        : Results.Conflict(new { title = "Une demande de jumelage active existe déjà pour une autre identité." });
}).WithName("CreateNodeEnrollmentRequest");

nodes.MapGet("/enrollment-requests", async (
    NodeRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListPendingPairingRequestsAsync(cancellationToken)))
    .WithName("ListPendingNodeEnrollmentRequests");

nodes.MapPost("/enrollment-requests/{nodeId:guid}/approve", async (
    Guid nodeId,
    NodeRepository repository,
    CancellationToken cancellationToken) =>
{
    var node = await repository.ApprovePairingRequestAsync(nodeId, cancellationToken);
    return node is null
        ? (IResult)Results.NotFound(new { title = "Cette demande est expirée ou n’existe plus." })
        : Results.Ok(node);
}).WithName("ApprovePendingNodeEnrollmentRequest");

nodes.MapPost("/enrollment-requests/approve", async (
    ApproveNodeEnrollmentRequest request,
    NodeRepository repository,
    CancellationToken cancellationToken) =>
{
    var errors = NodeHeartbeatValidator.Validate(request);
    if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
    var node = await repository.ApprovePairingRequestAsync(request.PairingKey, cancellationToken);
    return node is null
        ? Results.NotFound(new { title = "Cette clé de jumelage est invalide, expirée ou déjà utilisée." })
        : Results.Ok(node);
}).WithName("ApproveNodeEnrollmentRequest");

nodes.MapPut("/{nodeId:guid}/heartbeat", async (
    Guid nodeId,
    NodeHeartbeatRequest request,
    NodeRepository repository,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var providedToken = httpContext.Request.Headers["X-Node-Token"].ToString();
    if (string.IsNullOrWhiteSpace(providedToken))
    {
        return (IResult)Results.Unauthorized();
    }

    var errors = NodeHeartbeatValidator.Validate(request);
    if (errors.Count > 0)
    {
        return (IResult)Results.ValidationProblem(errors);
    }

    var node = await repository.RecordHeartbeatAsync(nodeId, providedToken, request, cancellationToken);
    return node is null ? (IResult)Results.Unauthorized() : Results.Ok(node);
}).WithName("RecordNodeHeartbeat");

nodes.MapGet("", async (NodeRepository repository, NodePresenceOptions options, CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListAsync(options.OfflineAfter, cancellationToken)))
    .WithName("ListNodes");

nodes.MapGet("/{nodeId:guid}", async (Guid nodeId, NodeRepository repository, NodePresenceOptions options, CancellationToken cancellationToken) =>
{
    var node = await repository.GetAsync(nodeId, options.OfflineAfter, cancellationToken);
    return node is null ? (IResult)Results.NotFound() : Results.Ok(node);
}).WithName("GetNode");


var executions = app.MapGroup("/api/v1/executions").WithTags("Executions");
executions.MapGet("", async (ExecutionRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.OverviewAsync(ct))).WithName("GetExecutionOverview");
executions.MapGet("/{id:guid}", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
{
    var execution = await repository.GetAsync(id, ct);
    return execution is null ? (IResult)Results.NotFound() : Results.Ok(execution);
}).WithName("GetExecution");
executions.MapGet("/{id:guid}/events", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
    await repository.GetAsync(id, ct) is null ? (IResult)Results.NotFound() : Results.Ok(await repository.EventsAsync(id, ct))).WithName("GetExecutionEvents");
executions.MapPost("", async (CreateExecutionRequest request, LibraryRepository libraryRepository, RuntimeRepository runtime, FlowDefinitionValidator validator, FlowRepository flowRepository, CancellationToken ct) =>
{
    var errors = new Dictionary<string, string[]>();
    if (request.Id == Guid.Empty) errors["id"] = ["Un identifiant unique est obligatoire."];
    if (string.IsNullOrWhiteSpace(request.Media) || request.Media.Length > 2048) errors["media"] = ["Le média doit contenir entre 1 et 2048 caractères."];
    if (string.IsNullOrWhiteSpace(request.Library) || request.Library.Length > 128) errors["library"] = ["La bibliothèque doit contenir entre 1 et 128 caractères."];
    if (string.IsNullOrWhiteSpace(request.Flow) || request.Flow.Length > 128) errors["flow"] = ["Le flow doit contenir entre 1 et 128 caractères."];
    if (request.FlowVersion <= 0) errors["flowVersion"] = ["La version doit être positive."];
    if (request.FlowDefinition.ValueKind != JsonValueKind.Object) errors["flowDefinition"] = ["La définition doit être un objet JSON."];
    else if (request.FlowDefinition.GetRawText().Length > 1048576) errors["flowDefinition"] = ["La définition dépasse 1 Mo."];
    if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
    var (definition, validation) = validator.ValidateDocument(request.FlowDefinition);
    if (!validation.IsValid) return Results.UnprocessableEntity(validation);
    if (definition!.Version != request.FlowVersion)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["flowVersion"] = ["La version doit correspondre à celle de la définition."] });
    var storedFlow = await flowRepository.GetAsync(definition.Id,definition.Version,ct);
    if (storedFlow is null) return Results.Conflict(new { title="La version du flow doit être enregistrée avant de créer une exécution." });
    var canonical = JsonSerializer.SerializeToElement(storedFlow.Definition,FlowRepository.JsonOptions);
    if (!JsonElement.DeepEquals(canonical,JsonSerializer.SerializeToElement(definition,FlowRepository.JsonOptions)))
        return Results.Conflict(new { title="La définition ne correspond pas à la version enregistrée." });
    request = request with { Flow=storedFlow.Definition.Name, FlowDefinition=canonical };
    var library=(await libraryRepository.ListAsync(ct)).SingleOrDefault(l=>l.Name==request.Library);
    if(library is null) return Results.NotFound(new { title="Bibliothèque introuvable." });
    try {
        return await runtime.EnqueueAsync(new(request.Id,library.Id,request.Media,definition.Id,definition.Version),storedFlow.Definition,ct)
            ? Results.Created($"/api/v1/executions/{request.Id}",new { request.Id }) : Results.Conflict(new { title="Identifiant déjà utilisé ou bibliothèque inactive." });
    } catch(ArgumentException e) { return Results.BadRequest(new { title=e.Message }); }
}).WithName("RegisterExecution");
executions.MapPost("/{id:guid}/cancel", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
    await repository.CancelAsync(id, ct) ? Results.NoContent() : Results.Conflict(new { title = "Cette exécution ne peut pas être annulée dans son état actuel." }))
    .WithName("CancelExecution");
executions.MapPost("/{id:guid}/retry", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
{
    var retryId = await repository.RetryAsync(id, ct);
    return retryId is null ? (IResult)Results.Conflict(new { title = "Relance impossible ou déjà enregistrée pour cette tentative." })
        : Results.Created($"/api/v1/executions/{retryId}", new { id = retryId });
}).WithName("RetryExecution");

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { }
