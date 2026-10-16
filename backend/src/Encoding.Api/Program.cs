using Encoding.Api.Infrastructure.Data;
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
using Encoding.Core.Nodes;
using Encoding.Contracts.Libraries;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Encoding.Api.Infrastructure.PeerCluster;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

if (args.FirstOrDefault() == DatabaseTransferCommand.Name)
    Environment.Exit(await DatabaseTransferCommand.RunAsync(args[1..], AppContext.BaseDirectory, Console.Out, CancellationToken.None));

var builder = WebApplication.CreateBuilder(args);
builder.AddOperations();
var peerClusterOptions = builder.AddPeerCluster();
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
builder.Services.AddSingleton<System.Data.Common.DbDataSource>(DatabaseProvider.Create(builder.Configuration, connectionString));
builder.Services.AddSingleton(new NodePresenceOptions(TimeSpan.FromSeconds(offlineAfterSeconds)));
builder.Services.AddSingleton<NodeRepository>();
builder.Services.AddSingleton<NodeGroupRepository>();
builder.Services.AddSingleton<EnrollmentWindowRepository>();
builder.Services.AddSingleton<FirstLinkRepository>();
builder.Services.AddSingleton<LocalNodeRegistry>();
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
builder.Services.AddTransient<LocalAgentAuthenticationHandler>();
builder.Services.AddHttpClient(Encoding.Api.Nodes.LocalNodeEndpoints.HttpClientName, (services, client) =>
{
    client.BaseAddress = new Uri(Encoding.Api.Nodes.LocalNodeEndpoints.AgentBaseUrl(services.GetRequiredService<IConfiguration>()));
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<LocalAgentAuthenticationHandler>();
builder.Services.AddHttpClient("PluginConnectionTest", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<RuntimeEligibility>();
builder.Services.AddSingleton<RuntimeRepository>();
builder.Services.AddSingleton<DiskLockRepository>();
builder.Services.AddSingleton<Encoding.Api.Plugins.PublicationGuardProvider>();
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
builder.Services.AddNodeModeControl(builder.Configuration[NodeModeEndpoints.ControlUrlSetting]);
builder.Services.AddHostedService<ClusterDiscoveryPublisher>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
// Fail startup if the bundled installed catalog is invalid or missing.
app.Services.GetRequiredService<FlowComponentRegistry>();
await DatabaseMigrator.ApplyAsync(app.Services.GetRequiredService<System.Data.Common.DbDataSource>(), app.Environment.ContentRootPath, app.Lifetime.ApplicationStopping, app.Logger);
if (peerClusterOptions.Enabled)
{
    await app.RestoreStateAsync<PeerStateMachine>();
    app.Services.GetRequiredService<PeerTrustStore>().AttachStateMachine(app.Services.GetRequiredService<PeerStateMachine>());
}

if (peerClusterOptions.Enabled)
{
    var peerTrustStore = app.Services.GetRequiredService<PeerTrustStore>();
    app.UseWhen(context => context.Request.Path == "/api/v1/peer-cluster/presence"
        && context.Connection.LocalPort != peerClusterOptions.ListenPort, branch =>
        branch.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }));
    app.UseWhen(context => context.Connection.LocalPort == peerClusterOptions.ListenPort, branch =>
    {
        branch.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path != "/cluster-consensus/raft" && path != "/api/v1/peer-cluster/presence")
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (path == "/api/v1/peer-cluster/presence"
                && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                bodySize.MaxRequestBodySize = 4 * 1024 * 1024;
            var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            if (!peerTrustStore.IsTrustedClientCertificate(certificate))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        branch.UseConsensusProtocolHandler();
    });
}

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
app.MapPeerClusterEndpoints();
app.MapNodeEndpoints();
app.MapNodeGroupEndpoints();
app.MapLocalNodeEndpoints();
app.MapNodeMode("/api/v1/node/mode");
app.MapExecutionEndpoints();

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { }
