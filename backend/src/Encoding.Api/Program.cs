using Encoding.Api.Auth;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Operations;
using Encoding.Api.Nodes;
using Encoding.Api.Libraries;
using Encoding.Api.Plugins;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Libraries;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.AddOperations();
var pluginKeyDirectory = builder.Configuration["PluginSettings:KeyDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "plugin-keys");
Directory.CreateDirectory(pluginKeyDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(pluginKeyDirectory))
    .SetApplicationName("Encoding.PluginSettings");
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
builder.Services.AddSingleton<NodeGroupRepository>();
builder.Services.AddSingleton<EnrollmentWindowRepository>();
builder.Services.AddClusterAuthentication(builder.Configuration);
builder.Services.AddSingleton<ExecutionRepository>();
builder.Services.AddSingleton<FlowComponentRegistry>();
builder.Services.AddSingleton<FlowDefinitionValidator>();
builder.Services.AddSingleton<FlowSimulator>();
builder.Services.AddSingleton<FlowMetadataImporter>();
builder.Services.AddSingleton<FlowRepository>();
builder.Services.AddSingleton<FlowArchiveService>();
builder.Services.AddSingleton<FlowScriptRepository>();
builder.Services.AddSingleton<FlowTemplateRepository>();
builder.Services.AddSingleton<FlowComponentPackRepository>();
builder.Services.AddSingleton<EncodingProfileRepository>();
builder.Services.AddSingleton<PluginConfigurationRepository>();
builder.Services.AddSingleton<PluginConnectionTester>();
builder.Services.AddSingleton<IntegrationTriggerRepository>();
builder.Services.AddSingleton<IntegrationTriggerService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<IntegrationTriggerService>());
builder.Services.AddHttpClient("PluginConnectionTest", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<RuntimeEligibility>();
builder.Services.AddSingleton<RuntimeRepository>();
builder.Services.AddSingleton<RuntimeArtifactStore>();
builder.Services.AddSingleton<RuntimeTransferRepository>();
builder.Services.AddSingleton<SourceReplacementRepository>();
builder.Services.AddSingleton<ExecutionWaitRepository>();
builder.Services.AddHostedService<RuntimeRecoveryService>();
builder.Services.AddHostedService<ExecutionWaitService>();
builder.Services.AddHostedService<FlowBackupService>();
builder.Services.AddSingleton<LibraryRepository>();
builder.Services.AddSingleton<LibraryWorkflowRepository>();
builder.Services.AddSingleton<LibraryProcessingRepository>();
builder.Services.AddHostedService<LibraryMonitoringService>();
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
await DatabaseMigrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Environment.ContentRootPath, app.Lifetime.ApplicationStopping, app.Logger);

app.UseRequestObservability();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseClusterAuthentication();
app.MapAuthEndpoints();
app.MapEnrollmentWindowEndpoints();
app.MapFlowEndpoints();
app.MapFlowScriptEndpoints();
app.MapFlowTemplateEndpoints();
app.MapFlowComponentPackEndpoints();
app.MapEncodingProfileEndpoints();
app.MapFlowSimulationEndpoints();
app.MapPluginConfigurationEndpoints();
app.MapArrWebhookEndpoints();
app.MapRuntimeEndpoints();
app.MapSourceReplacementEndpoints();

app.MapOperationsEndpoints();

app.MapLibraryWorkflowEndpoints();
app.MapLibraryEndpoints();
app.MapClusterEndpoints();
app.MapNodeEndpoints();
app.MapNodeGroupEndpoints();
app.MapExecutionEndpoints();

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { }
