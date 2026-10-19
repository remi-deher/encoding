using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Tools;
using Encoding.Api.Nodes;
using Encoding.Contracts.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiClusterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task AvailabilityUsesDisplayNamesForLocalAndRemoteNodesWithTechnicalNameFallback()
    {
        await using var mesh = await Mesh.CreateAsync();
        Assert.Equal("first", (await mesh.First.Service.LocalAsync(Ct)).Name);
        await mesh.First.LocalNode.RequestNameAsync("Salon", Ct);
        await mesh.Second.LocalNode.RequestNameAsync("Bureau", Ct);

        var view = await mesh.First.Service.ViewAsync(Ct);
        Assert.Contains(view.Nodes, node => node.NodeId == mesh.First.Identity.PeerId && node.Name == "Salon");
        Assert.Contains(view.Nodes, node => node.NodeId == mesh.Second.Identity.PeerId && node.Name == "Bureau");
    }

    [Fact]
    public async Task AnyPeerCanUseRemoteProviderAndResumeDurableApplicationContextWithoutCopyingSecrets()
    {
        await using var mesh = await Mesh.CreateAsync();
        var requestId = Guid.NewGuid();
        var first = await mesh.First.Service.GenerateAsync("openai", new("instructions", "first question", RequestId: requestId), Ct);
        Assert.True(first.Success, first.ErrorMessage);
        Assert.NotNull(first.SessionId);
        Assert.Equal(1, mesh.Factory.Inferences);
        Assert.Null(mesh.First.Settings.GetSettings().OpenAiApiKey);
        var replay = await mesh.First.NewService().GenerateAsync("openai", new("instructions", "first question", RequestId: requestId), Ct);
        Assert.Equal(first.Content, replay.Content);
        Assert.Equal(1, mesh.Factory.Inferences);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", new("instructions", "changed question", SessionId: first.SessionId, RequestId: requestId), Ct));
        var continuation = await mesh.Second.Service.GenerateAsync("openai", new("instructions", "second question", SessionId: first.SessionId), Ct);
        Assert.True(continuation.Success, continuation.ErrorMessage);
        Assert.Equal(2, mesh.Factory.Inferences);
        Assert.Contains("first question", mesh.Factory.LastPrompt);
        Assert.Contains("answer from remote model", mesh.Factory.LastPrompt);
        Assert.Contains("second question", mesh.Factory.LastPrompt);
        var events = await mesh.First.Journal.GetEventsAfterAsync(mesh.ClusterId, new Dictionary<Guid, long>(), 32, Ct);
        Assert.DoesNotContain("first question", JsonSerializer.Serialize(events));
        await mesh.Second.Journal.ApplyIncomingAsync(events, Ct);
        var checkpoint = await mesh.Second.Service.GetSessionAsync(first.SessionId!, Ct);
        Assert.NotNull(checkpoint);
        Assert.Equal(2, checkpoint.Turns.Count);
        Assert.Null(checkpoint.PendingRequest);
        Assert.DoesNotContain("private-api-key", JsonSerializer.Serialize(checkpoint));
        var fork = await mesh.Second.Service.ForkSessionAsync(first.SessionId!, Ct);
        Assert.StartsWith(mesh.Second.Identity.PeerId.ToString("N"), fork.Id);
        Assert.Equal(2, fork.Turns.Count);
        Assert.Null(fork.PendingRequest);
    }

    [Fact]
    public async Task SessionHistoryIsBoundedAndReportsPendingAndCompletedWorkWithoutPrompts()
    {
        await using var mesh = await Mesh.CreateAsync();
        Assert.Empty((await mesh.First.Service.ListSessionsAsync(0, Ct)).Items);
        var first = await mesh.First.Service.GenerateAsync("openai", new("private instructions", "private question"), Ct);
        var page = await mesh.First.Service.ListSessionsAsync(0, Ct);
        var summary = Assert.Single(page.Items);
        Assert.Equal(first.SessionId, summary.Id);
        Assert.Equal("completed", summary.Status);
        Assert.Equal(1, summary.TurnCount);
        Assert.NotNull(summary.UpdatedAt);
        Assert.Null(summary.Usage);
        Assert.DoesNotContain("private question", JsonSerializer.Serialize(page));
        Assert.DoesNotContain("private instructions", JsonSerializer.Serialize(page));
        var id = $"{mesh.First.Identity.PeerId:N}.{Guid.NewGuid():N}";
        await mesh.First.Store.SaveAsync("session/" + id, new AiSessionState(id, "openai", "system", [],
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1)), Ct);
        Assert.Contains((await mesh.First.Service.ListSessionsAsync(0, Ct)).Items, item => item.Id == id && item.Status == "running");
        Assert.Empty((await mesh.First.Service.ListSessionsAsync(50, Ct)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.ListSessionsAsync(-1, Ct));
    }

    [Fact]
    public async Task SessionsCannotMixMediaOrOperationsAndPreserveUsageOnReplay()
    {
        await using var mesh = await Mesh.CreateAsync();
        var scope = new AiSessionScope("subtitle-translation", LibraryId: Guid.NewGuid(), MediaId: Guid.NewGuid());
        var prompt = new AiPrompt("instructions", "question", Scope: scope);
        var first = await mesh.First.Service.GenerateAsync("openai", prompt, Ct);
        Assert.True(first.Success);
        var session = await mesh.First.Service.GetSessionAsync(first.SessionId!, Ct);
        var usage = new AiUsage(100, 20);
        await mesh.First.Store.SaveAsync("session/" + first.SessionId, session! with {
            Turns = [session!.Turns[0] with { Usage = usage }]
        }, Ct);
        var replay = await mesh.First.NewService().GenerateAsync("openai", prompt with { SessionId = first.SessionId, RequestId = first.RequestId }, Ct);
        Assert.Equal(usage, replay.Usage);
        Assert.Equal(usage, Assert.Single((await mesh.First.Service.ListSessionsAsync(0, Ct)).Items).Usage);
        Assert.Equal(1, mesh.Factory.Inferences);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai",
            prompt with { SessionId = first.SessionId, Scope = scope with { MediaId = Guid.NewGuid() } }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai",
            prompt with { SessionId = first.SessionId, Model = "another-model" }, Ct));
    }

    [Fact]
    public async Task QuotasUseSignedPeerTransportAndStaySeparatedByNode()
    {
        await using var mesh = await Mesh.CreateAsync();
        var quotas = await mesh.First.Service.QuotasAsync(Ct);
        Assert.Equal(2, quotas.Nodes.Count);
        Assert.All(quotas.Nodes, node => {
            Assert.Equal(2, node.Providers.Count);
            Assert.All(node.Providers, provider => Assert.Equal("unavailable", provider.State));
        });
        Assert.DoesNotContain("private-api-key", JsonSerializer.Serialize(quotas));
    }

    [Fact]
    public async Task FailedExecutorFallsBackToAnotherConnectedPeerAndCommitsOnlyOneTurn()
    {
        await using var mesh = await Mesh.CreateAsync();
        mesh.First.Settings.UpdateSettings(new(OpenAiApiKey: "broken-test-key"));
        var result = await mesh.First.Service.GenerateAsync("openai", new("instructions", "question"), Ct);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, mesh.Factory.Inferences);
        var session = await mesh.First.Service.GetSessionAsync(result.SessionId!, Ct);
        Assert.Single(session!.Turns);
        Assert.Null(session.PendingRequest);
    }

    [Fact]
    public async Task RestartedCoordinatorDoesNotStartAnotherAttemptBeforeLeaseExpires()
    {
        await using var mesh = await Mesh.CreateAsync();
        var sessionId = $"{mesh.First.Identity.PeerId:N}.{Guid.NewGuid():N}";
        var requestId = Guid.NewGuid();
        var prompt = new AiPrompt("instructions", "question", SessionId: sessionId, RequestId: requestId);
        var pending = new AiSessionState(sessionId, "openai", "instructions", [], requestId, DateTimeOffset.UtcNow.AddMinutes(1), prompt);
        await mesh.First.Store.SaveAsync("session/" + sessionId, pending, Ct);
        var result = await mesh.First.NewService().GenerateAsync("openai", prompt, Ct);
        Assert.False(result.Success);
        Assert.Contains("bail", result.ErrorMessage);
        Assert.Equal(0, mesh.Factory.Inferences);
        await mesh.First.Store.SaveAsync("session/" + sessionId, pending with { AttemptDeadline = DateTimeOffset.UtcNow.AddMinutes(-1) }, Ct);
        var recovered = await mesh.First.NewService().GenerateAsync("openai", prompt, Ct);
        Assert.True(recovered.Success, recovered.ErrorMessage);
        Assert.Equal(1, mesh.Factory.Inferences);
    }

    [Fact]
    public async Task UnsignedMeshRequestsAndExpiredAttemptsCannotInvokeModel()
    {
        await using var mesh = await Mesh.CreateAsync();
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream("{}"u8.ToArray());
        Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(await mesh.Second.Transport.ReceiveAsync(http, mesh.Second.Service, Ct));
        var request = new AiExecuteRequest(Guid.NewGuid(), "openai", new("system", "user"), DateTimeOffset.UtcNow.AddSeconds(-1));
        var result = (AiCompletionResult)await mesh.Second.Service.DispatchAsync("execute", JsonSerializer.SerializeToElement(request), Ct);
        Assert.False(result.Success);
        Assert.Equal(0, mesh.Factory.Inferences);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.Second.Service.DispatchAsync("save-token", JsonSerializer.SerializeToElement(new { token = "x" }), Ct));
    }

    [Fact]
    public async Task ModelCatalogAndPolicyUsePeerTransportAndNoCredentialsAreExposed()
    {
        await using var mesh = await Mesh.CreateAsync();
        var catalog = await mesh.First.Service.ModelsAsync(false, Ct);
        var remote = Assert.Single(catalog.Nodes, n => n.NodeId == mesh.Second.Identity.PeerId);
        Assert.Equal(2, Assert.Single(remote.Providers).Models.Count);
        Assert.DoesNotContain("private-api-key", JsonSerializer.Serialize(catalog));
        await mesh.First.Service.UpdateModelPolicyAsync(new("openai", "other-model", false), Ct);
        await mesh.First.Service.UpdateModelPolicyAsync(new("openai", "gpt-4o-mini", false), Ct);
        Assert.Equal(2, (await mesh.First.Service.ModelPolicyAsync(Ct)).DisabledModels.Count);
        var events = await mesh.First.Journal.GetEventsAfterAsync(mesh.ClusterId, new Dictionary<Guid, long>(), 32, Ct);
        await mesh.Second.Journal.ApplyIncomingAsync(events, Ct);
        Assert.Equal(2, (await mesh.Second.Service.ModelPolicyAsync(Ct)).DisabledModels.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", new("s", "u", Model: "other-model"), Ct));
        await mesh.First.Service.UpdateModelPolicyAsync(new("openai", "other-model", true), Ct);
        Assert.Single((await mesh.First.Service.ModelPolicyAsync(Ct)).DisabledModels);
    }

    [Fact]
    public async Task ConversationTurnsCanChangeModelsButReplayAndPendingRequestsKeepTheirSelection()
    {
        await using var mesh = await Mesh.CreateAsync();
        var node = mesh.Second.Identity.PeerId;
        var firstPrompt = new AiPrompt("instructions", "first", Model: "gpt-4o-mini", NodeId: node);
        var first = await mesh.First.Service.GenerateAsync("openai", firstPrompt, Ct, allowModelChanges: true);
        Assert.True(first.Success, first.ErrorMessage);
        var next = new AiPrompt("instructions", "second", Model: "other-model", SessionId: first.SessionId, RequestId: Guid.NewGuid(), NodeId: node);
        var second = await mesh.Second.Service.GenerateAsync("openai", next, Ct, allowModelChanges: true);
        Assert.True(second.Success, second.ErrorMessage);
        var session = await mesh.First.Service.GetSessionAsync(first.SessionId!, Ct);
        Assert.Equal("gpt-4o-mini", session!.Turns[0].Selection!.Model);
        Assert.Equal("other-model", session.Turns[1].Selection!.Model);
        Assert.Equal(node, session.Turns[1].ExecutorNodeId);
        Assert.Contains("first", mesh.Factory.LastPrompt);
        var replay = await mesh.First.Service.GenerateAsync("openai", firstPrompt with { SessionId = first.SessionId, RequestId = first.RequestId }, Ct, allowModelChanges: true);
        Assert.Equal(first.Content, replay.Content);
        Assert.Equal(2, mesh.Factory.Inferences);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", firstPrompt with { Model = "other-model", SessionId = first.SessionId, RequestId = first.RequestId }, Ct, allowModelChanges: true));
        var pendingId = Guid.NewGuid();
        var pendingPrompt = next with { UserPrompt = "third", RequestId = pendingId };
        await mesh.First.Store.SaveAsync("session/" + first.SessionId, session with {
            PendingRequest = pendingId, PendingPrompt = pendingPrompt, AttemptDeadline = DateTimeOffset.UtcNow.AddMinutes(1)
        }, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", pendingPrompt with { Model = "gpt-4o-mini" }, Ct, allowModelChanges: true));
    }

    [Fact]
    public async Task AssistantOptionsUseTheDiscoveredCatalogAndFilterDisabledModels()
    {
        await using var mesh = await Mesh.CreateAsync();
        var assistant = new AiAssistantService(mesh.First.Store, mesh.First.Service, mesh.First.Transport,
            null!, null!, null!, null!, null!, null!, null!, new EphemeralDataProtectionProvider());
        var options = await assistant.OptionsAsync(Ct);
        Assert.Equal(2, options.Models.Count);
        Assert.All(options.Models, option => {
            Assert.Equal(mesh.Second.Identity.PeerId, option.NodeId);
            Assert.Empty(option.ReasoningEfforts);
            Assert.False(option.ReasoningKnown);
        });
        await mesh.First.Service.UpdateModelPolicyAsync(new("openai", "other-model", false), Ct);
        Assert.Single((await assistant.OptionsAsync(Ct)).Models);
    }

    [Fact]
    public async Task AssistantPersistsPerTurnSelectionsAndObservedModelWithoutRewritingOldTurns()
    {
        await using var mesh = await Mesh.CreateAsync();
        mesh.Factory.ReportModel = true;
        var resourceId = Guid.NewGuid();
        var nodes = new NodeRepository(mesh.First.Database.Db);
        await nodes.EnrollAsync(new(resourceId, "worker", [Encoding.Contracts.Nodes.NodeRole.Worker], 1, [], "node-secret"), true, Ct);
        var assistant = new AiAssistantService(mesh.First.Store, mesh.First.Service, mesh.First.Transport,
            null!, null!, null!, null!, nodes, new NodePresenceOptions(TimeSpan.FromMinutes(2)), null!, new EphemeralDataProtectionProvider());
        mesh.First.Assistant = assistant;
        var remoteAssistant = new AiAssistantService(mesh.Second.Store, mesh.Second.Service, mesh.Second.Transport,
            null!, null!, null!, null!, null!, null!, null!, new EphemeralDataProtectionProvider());
        mesh.Second.Assistant = remoteAssistant;
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.Role, "admin")], "test"));
        var request = new AiAssistantMessageRequest(Guid.NewGuid(), "first question", ResourceType: "node", ResourceId: resourceId,
            Provider: "openai", Model: "gpt-4o-mini", NodeId: mesh.Second.Identity.PeerId);
        var first = await assistant.SendAsync(request, user, Ct);
        Assert.True(first!.Success, first.ErrorMessage);
        Assert.Equal("resolved-test-model", first.ActualModel);
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.SendAsync(request with { Model = "other-model" }, user, Ct));
        var second = await remoteAssistant.SendAsync(new(Guid.NewGuid(), "second question", ConversationId: first.Conversation.Id,
            Provider: "openai", Model: "other-model", NodeId: mesh.Second.Identity.PeerId), user, Ct);
        Assert.True(second!.Success, second.ErrorMessage);
        var detail = await assistant.GetAsync(first.Conversation.Id, user, Ct);
        Assert.Equal("gpt-4o-mini", detail!.Turns[0].Selection!.Model);
        Assert.Equal("other-model", detail.Turns[1].Selection!.Model);
        Assert.Equal("other-model", detail.Conversation.Model);
        Assert.All(detail.Turns, turn => Assert.Equal("resolved-test-model", turn.ActualModel));
        var replay = await assistant.SendAsync(request with { ConversationId = first.Conversation.Id }, user, Ct);
        Assert.True(replay!.Success);
        Assert.Equal("other-model", replay.Conversation.Model);
        Assert.Equal("other-model", (await assistant.GetAsync(first.Conversation.Id, user, Ct))!.Conversation.Model);

        await Assert.ThrowsAsync<ArgumentException>(() => assistant.SendAsync(request with { ConversationId = first.Conversation.Id, Model = "other-model" }, user, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => remoteAssistant.SendAsync(new(Guid.NewGuid(), "invalid effort", ConversationId: first.Conversation.Id,
            Provider: "openai", Model: "other-model", ReasoningEffort: "high", NodeId: mesh.Second.Identity.PeerId), user, Ct));
        mesh.Factory.BlockInferences = true;
        var cancelledRequest = Guid.NewGuid();
        var sending = remoteAssistant.SendAsync(new(cancelledRequest, "third question", ConversationId: first.Conversation.Id,
            Provider: "openai", Model: "other-model", NodeId: mesh.Second.Identity.PeerId), user, Ct);
        await mesh.Factory.InferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await remoteAssistant.CancelRequestAsync(cancelledRequest, user, Ct));
        Assert.False((await sending)!.Success);
        Assert.Equal(2, (await assistant.GetAsync(first.Conversation.Id, user, Ct))!.Turns.Count);

    }

    private sealed class Node : IAsyncDisposable
    {
        public TestDatabase Database = null!;
        public AiCliTestProfile Profile = null!;
        public ClusterPeerIdentityStore Identity = null!;
        public ClusterDataKeyStore Key = null!;
        public ClusterPeerAdmissionRepository Admissions = null!;
        public ClusterReconciliationRepository Journal = null!;
        public AiClusterStore Store = null!;
        public AiClusterTransport Transport = null!;
        public AiClusterService Service = null!;
        public DeviceCodeAuthService Auth = null!;
        public AiSettingsService Settings = null!;
        public AiClientFactory Clients = null!;
        public AiModelCatalogService Catalog = null!;
        public AiAssistantService? Assistant;
        public IConfiguration Configuration = null!;
        public LocalNodeRegistry LocalNode = null!;
        public static async Task<Node> CreateAsync(Guid cluster, string name, string other, MeshFactory factory, byte[] key, bool connected)
        {
            var node = new Node { Database = await TestDatabase.CreateSqliteAsync("ai-cluster"), Profile = new() };
            await DatabaseMigrator.ApplyAsync(node.Database.Db, Harness.RepositoryRoot(), Ct);
            node.Configuration = new ConfigurationBuilder().AddConfiguration(node.Profile.Configuration).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Node:Name"] = name, ["Cluster:Reconciliation:Peers:0"] = "http://" + other + "/",
                ["Cluster:Reconciliation:AdvertisedApiBaseUrl"] = "http://" + name + "/",
                ["AiTools:OpenAi:ApiKey"] = connected ? "private-api-key" : null
            }).Build();
            var clusterIdentity = new ClusterIdentityRepository(node.Database.Db);
            await clusterIdentity.AdoptAsync(cluster, "AI tests", Ct);
            node.Identity = new(node.Configuration, new EphemeralDataProtectionProvider());
            node.Key = new(cluster, key);
            node.Admissions = new(node.Database.Db, clusterIdentity, node.Identity, node.Key);
            node.Journal = new(node.Database.Db);
            node.Store = new(node.Database.Db, node.Journal, clusterIdentity, node.Key);
            node.Transport = new(node.Identity, clusterIdentity, node.Admissions, node.Journal, new(), node.Store, factory, node.Configuration);
            node.Auth = new(node.Profile.Runtime);
            node.Settings = new(node.Configuration);
            node.Clients = new(node.Configuration, factory, node.Settings, node.Profile.Runtime);
            node.Catalog = new(node.Profile.Runtime, node.Settings, factory);
            node.LocalNode = new(new NodeRepository(node.Database.Db));
            node.Service = node.NewService();
            return node;
        }
        public AiClusterService NewService() => new(Transport, Store, Auth, Settings, Clients, Configuration, LocalNode, new AiQuotaService(Profile.Runtime), Catalog,
            new ServiceCollection().AddSingleton<AiAssistantService>(_ => Assistant ?? throw new InvalidOperationException("Assistant not configured")).BuildServiceProvider());
        public async ValueTask DisposeAsync() { Auth.Dispose(); Identity.Dispose(); Key.Dispose(); await Database.DisposeAsync(); Profile.Dispose(); }
    }
    private sealed class Mesh : IAsyncDisposable
    {
        public Guid ClusterId = Guid.NewGuid();
        public Node First = null!;
        public Node Second = null!;
        public MeshFactory Factory = new();
        public static async Task<Mesh> CreateAsync()
        {
            var mesh = new Mesh();
            var key = RandomNumberGenerator.GetBytes(32);
            mesh.First = await Node.CreateAsync(mesh.ClusterId, "first", "second", mesh.Factory, key, false);
            mesh.Second = await Node.CreateAsync(mesh.ClusterId, "second", "first", mesh.Factory, key, true);
            mesh.Factory.Nodes["first"] = mesh.First;
            mesh.Factory.Nodes["second"] = mesh.Second;
            var nodeId = Guid.NewGuid();
            await mesh.First.Admissions.EnsureRootAdmissionAsync(mesh.ClusterId, Ct);
            await mesh.First.Admissions.CreateDirectApprovalGrantAsync(nodeId, mesh.Second.Identity.PeerId, mesh.Second.Identity.PublicKey, Ct);
            var bootstrap = await mesh.First.Admissions.GetBootstrapForNodeAsync(nodeId, Ct);
            await mesh.Second.Admissions.ImportBootstrapAsync(bootstrap!, nodeId, Ct);
            return mesh;
        }
        public async ValueTask DisposeAsync() { await First.DisposeAsync(); await Second.DisposeAsync(); }
    }
    private sealed class MeshFactory : IHttpClientFactory
    {
        public readonly Dictionary<string, Node> Nodes = [];
        public int Inferences;
        public bool ReportModel;
        public bool BlockInferences;
        public TaskCompletionSource InferenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string LastPrompt = "";
        public HttpClient CreateClient(string name) => new(new Handler(this, name)) { Timeout = Timeout.InfiniteTimeSpan };
        private sealed class Handler(MeshFactory factory, string name) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (name == "AiClient")
                {
                    if (request.Method == HttpMethod.Get)
                        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new[] { new { id = "gpt-4o-mini" }, new { id = "other-model" } } }) };
                    factory.Inferences++;
                    if (factory.BlockInferences)
                    {
                        factory.InferenceStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    factory.LastPrompt = await request.Content!.ReadAsStringAsync(ct);
                    if (request.Headers.Authorization?.Parameter == "broken-test-key")
                        return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Provider unavailable") };
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { model = factory.ReportModel ? "resolved-test-model" : null, choices = new[] { new { message = new { content = "answer from remote model" } } } }) };
                }
                var node = factory.Nodes[request.RequestUri!.Host];
                var http = new DefaultHttpContext();
                http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
                http.Request.Body = new MemoryStream(await request.Content!.ReadAsByteArrayAsync(ct));
                http.Response.Body = new MemoryStream();
                foreach (var header in request.Headers) http.Request.Headers[header.Key] = header.Value.ToArray();
                var result = await node.Transport.ReceiveAsync(http, node.Service, ct);
                await result.ExecuteAsync(http);
                var response = new HttpResponseMessage((HttpStatusCode)http.Response.StatusCode) { Content = new ByteArrayContent(((MemoryStream)http.Response.Body).ToArray()) };
                foreach (var header in http.Response.Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                return response;
            }
        }
    }
}
