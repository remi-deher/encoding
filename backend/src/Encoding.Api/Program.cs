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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Encoding.Api.Infrastructure.PeerCluster;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Encoding.Api.Logging;
using Encoding.Api.Mcp;
using Encoding.Api.Tools;
using Encoding.Core.Logging;

if (args.FirstOrDefault() is McpCommand.Name or "--" + McpCommand.Name)
    Environment.Exit(await McpCommand.RunAsync(args[1..], Console.In, Console.Out, CancellationToken.None));
if (args.FirstOrDefault() == PeerAuthorityDatabaseCommand.Name)
    Environment.Exit(await PeerAuthorityDatabaseCommand.RunAsync(args[1..], AppContext.BaseDirectory, Console.Out, CancellationToken.None));
if (args.FirstOrDefault() is { } peerCommand && PeerOperationsCommand.Names.Contains(peerCommand))
    Environment.Exit(await PeerOperationsCommand.RunAsync(peerCommand, args[1..], AppContext.BaseDirectory, Console.Out, CancellationToken.None));
if (args.FirstOrDefault() == "--isolated-process")
{
    Environment.Exit(Encoding.Core.Runtime.ProcessSandbox.Run(args));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddOperations();
var peerClusterOptions = builder.AddPeerCluster();
var pluginKeyDirectory = ClusterPeerIdentityStore.ResolveKeyDirectory(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(pluginKeyDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(pluginKeyDirectory))
    .SetApplicationName("Encoding.PluginSettings");
var offlineAfterSeconds = builder.Configuration.GetValue("NodePresence:OfflineAfterSeconds", 30);
if (offlineAfterSeconds <= 0)
{
    throw new InvalidOperationException("NodePresence:OfflineAfterSeconds doit être supérieur à zéro.");
}
var localDatabase = DatabaseProvider.CreateRuntime(builder.Configuration);
builder.Services.AddSingleton(new PeerLocalDatabase(localDatabase));
builder.Services.AddSingleton<System.Data.Common.DbDataSource>(services => peerClusterOptions.Enabled
    ? new PeerDataSource(services.GetRequiredService<PeerDatabaseCoordinator>()) : localDatabase);
builder.Services.AddSingleton(new NodePresenceOptions(TimeSpan.FromSeconds(offlineAfterSeconds)));
builder.Services.AddSingleton<NodeRepository>();
builder.Services.AddSingleton<NodeGroupRepository>();
builder.Services.AddSingleton<EnrollmentWindowRepository>();
builder.Services.AddSingleton<FirstLinkRepository>();
builder.Services.AddSingleton<LocalNodeRegistry>();
// Instances créées hors conteneur : le fournisseur de logs est résolu pendant l'initialisation de ILoggerFactory.
var systemLogRepository = new SystemLogRepository(new PeerLocalDatabase(localDatabase));
var systemLogSink = new SystemLogDbSink(systemLogRepository);
builder.Services.AddSingleton(systemLogRepository);
builder.Services.AddSingleton(systemLogSink);
builder.Services.AddSingleton<ISystemLogSink>(systemLogSink);
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemLogDbSink>());
var localNodeLogContext = new LocalNodeLogContext();
builder.Services.AddSingleton(localNodeLogContext);
builder.Services.AddSingleton<ISystemLogNodeContext>(localNodeLogContext);
builder.Services.AddSystemLogging();
builder.Services.AddApiProblemDetails();
builder.Services.AddClusterAuthentication(builder.Configuration);
builder.Services.AddSingleton<ApiTokenRepository>();
builder.Services.AddSingleton<McpSessionManager>();
builder.Services.AddSingleton<ExecutionStreamBroadcaster>();
builder.Services.AddSingleton<ExecutionRepository>();
builder.Services.AddSingleton<FlowComponentRegistry>();
builder.Services.AddSingleton<FlowDefinitionValidator>();
builder.Services.AddSingleton<FlowCatalogPromptBuilder>();
builder.Services.AddSingleton<FlowAiGeneratorService>();
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
builder.Services.AddSingleton<McpService>();
builder.Services.AddSingleton<IntegrationTriggerRepository>();
builder.Services.AddSingleton<IntegrationTriggerService>();
builder.Services.AddSingleton<AiCliRuntime>();
builder.Services.AddSingleton<AiClusterStore>();
builder.Services.AddSingleton<AiClusterTransport>();
builder.Services.AddSingleton<AiClusterService>();
builder.Services.AddSingleton<AiAssistantService>();
builder.Services.AddHostedService<AiAssistantRetentionService>();
builder.Services.AddHttpClient("AiCluster", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<DeviceCodeAuthService>();
builder.Services.AddSingleton<AiSettingsService>();
builder.Services.AddSingleton<AiClientFactory>();
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
builder.Services.AddSingleton<ClusterPeerIdentityStore>();
builder.Services.AddSingleton<ClusterDataKeyStore>();
builder.Services.AddSingleton<ClusterPeerAdmissionRepository>();
builder.Services.AddSingleton<ClusterReconciliationRepository>();
builder.Services.AddSingleton<ClusterEntityReconciliation>();
builder.Services.AddSingleton<ClusterReconciliationHealth>();
builder.Services.AddNodeModeControl(builder.Configuration[NodeModeEndpoints.ControlUrlSetting]);
builder.Services.AddHostedService<ClusterDiscoveryPublisher>();
builder.Services.AddHttpClient("ClusterReconciliation", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ClusterReconciliationService>();
builder.Services.AddHostedService<ClusterReconciliationService>(services => services.GetRequiredService<ClusterReconciliationService>());
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
// Fail startup if the bundled installed catalog is invalid or missing.
app.Services.GetRequiredService<FlowComponentRegistry>();
await DatabaseMigrator.ApplyAsync(localDatabase, app.Environment.ContentRootPath, app.Lifetime.ApplicationStopping, app.Logger);
if (peerClusterOptions.Enabled)
{
    await app.Services.GetRequiredService<PeerDatabaseCoordinator>().InitializeAsync(app.Lifetime.ApplicationStopping);
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
            if (path != "/cluster-consensus/raft" && path != "/api/v1/peer-cluster/presence"
                && path != PeerRequestRouting.StatusPath && !PeerRequestRouting.IsClusterRoute(path))
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
            var database = app.Services.GetRequiredService<PeerDatabaseCoordinator>();
            if (!PeerRequestRouting.Compatible(context, database))
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }
            if (path == PeerRequestRouting.StatusPath)
            {
                await context.Response.WriteAsJsonAsync(PeerRequestRouting.Status(database, app.Services.GetRequiredService<PeerIdentityStore>(),
                    app.Services.GetRequiredService<PeerConsensusService>()));
                return;
            }
            if (PeerRequestRouting.IsClusterRoute(path) && !PeerRequestRouting.ReadPrincipal(context))
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
app.UsePeerRequestRouting();
app.MapAuthEndpoints();
app.MapApiTokenEndpoints();
app.MapEnrollmentWindowEndpoints();
app.MapFlowEndpoints();
app.MapFlowAiEndpoints();
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
app.MapSystemLogEndpoints();

app.MapLibraryWorkflowEndpoints();
app.MapLibraryEndpoints();
app.MapClusterEndpoints();
app.MapPeerClusterEndpoints();
app.MapClusterReconciliationEndpoints();
app.MapNodeEndpoints();
app.MapNodeGroupEndpoints();
app.MapLocalNodeEndpoints();
app.MapNodeMode("/api/v1/node/mode");
app.MapExecutionEndpoints();
app.MapMcpEndpoints();
app.MapAiToolEndpoints();
app.MapAiAssistantEndpoints();
app.MapOpenApiEndpoints();

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { }
