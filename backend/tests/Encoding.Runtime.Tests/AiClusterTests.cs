using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Tools;
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
        public IConfiguration Configuration = null!;
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
            node.Service = node.NewService();
            return node;
        }
        public AiClusterService NewService() => new(Transport, Store, Auth, Settings, Clients, Configuration);
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
        public string LastPrompt = "";
        public HttpClient CreateClient(string name) => new(new Handler(this, name)) { Timeout = Timeout.InfiniteTimeSpan };
        private sealed class Handler(MeshFactory factory, string name) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (name == "AiClient")
                {
                    factory.Inferences++;
                    factory.LastPrompt = await request.Content!.ReadAsStringAsync(ct);
                    if (request.Headers.Authorization?.Parameter == "broken-test-key")
                        return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Provider unavailable") };
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "answer from remote model" } } } }) };
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
