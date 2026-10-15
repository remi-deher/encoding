using System.Net;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Un rattachement qui n'aboutit pas ne doit jamais laisser le nœud bloqué sur un cluster qui ne l'a pas accepté (architectures 0040 et 0045).</summary>
public sealed class WorkerPendingJoinTests
{
    private sealed class NoTools : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NodeToolCapability>>([]);
    }

    private static NodeAgentOptions Options(string directory) =>
        NodeAgentOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection([new("Node:DataDirectory", directory)]).Build());

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "encoding-pending-join-" + Guid.NewGuid().ToString("N"));

    private static (WorkerEnrollmentService Enrollment, WorkerIdentityStore Store, WorkerClusterDiscovery Discovery) Build(string directory)
    {
        var options = Options(directory);
        var store = new WorkerIdentityStore(options);
        var discovery = new WorkerClusterDiscovery(options, NullLogger<WorkerClusterDiscovery>.Instance);
        var json = Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions());
        return (new WorkerEnrollmentService(options, store, discovery, new NoTools(), NullLogger<WorkerEnrollmentService>.Instance, json), store, discovery);
    }

    /// <summary>Serveur de cluster minimal : répond à l'identité, puis à tout autre appel avec le statut demandé.</summary>
    private static (HttpListener Listener, string Url, Guid Id, Task Serving) Cluster(HttpStatusCode otherStatus)
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var id = Guid.NewGuid();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception) { return; }
                var identity = context.Request.Url!.AbsolutePath == "/api/v1/cluster";
                context.Response.StatusCode = identity ? 200 : (int)otherStatus;
                context.Response.ContentType = "application/json";
                var body = System.Text.Encoding.UTF8.GetBytes(identity ? $"{{\"id\":\"{id}\",\"name\":\"Maison\"}}" : "{}");
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        return (listener, $"http://127.0.0.1:{port}/", id, serving);
    }

    [Fact]
    public async Task RefusedDirectEnrollmentLeavesTheNodeStandalone()
    {
        var (enrollment, store, discovery) = Build(NewDirectory());
        var (listener, url, id, _) = Cluster(HttpStatusCode.Forbidden);
        using var _ = listener;
        await discovery.AddByAddressAsync(url, CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(() => enrollment.EnrollAsync(id, CancellationToken.None));

        var identity = await store.GetAsync(CancellationToken.None);
        Assert.False(identity.HasCredentials);
        Assert.Null(identity.ClusterId);
        Assert.Contains("refusé", identity.SwitchFailure);
    }

    [Fact]
    public async Task UnreachableClusterLeavesTheNodeStandaloneInsteadOfPending()
    {
        var (enrollment, store, discovery) = Build(NewDirectory());
        var (listener, url, id, _) = Cluster(HttpStatusCode.OK);
        await discovery.AddByAddressAsync(url, CancellationToken.None);
        listener.Stop();

        await Assert.ThrowsAsync<HttpRequestException>(() => enrollment.CreatePairingKeyAsync(id, CancellationToken.None));

        Assert.False((await store.GetAsync(CancellationToken.None)).HasCredentials);
    }

    [Fact]
    public async Task ExpiredPairingKeyReturnsTheNodeToStandalone()
    {
        var directory = NewDirectory();
        var (_, store, _) = Build(directory);
        await store.SavePairingAsync(Guid.NewGuid().ToString("D"), "Maison", "http://127.0.0.1:1/", CancellationToken.None);
        await store.MarkPairingPublishedAsync(CancellationToken.None);
        var (live, _, _) = Build(directory);
        Assert.False(await live.RollbackExpiredSwitchAsync(CancellationToken.None));

        // Le processus redémarre après l'échéance de la clé : l'état relu depuis le disque est périmé.
        var path = Path.Combine(directory, "node-identity.json");
        var edited = System.Text.RegularExpressions.Regex.Replace(await File.ReadAllTextAsync(path), "\"pairingExpiresAt\":\"[^\"]+\"", "\"pairingExpiresAt\":\"2020-01-01T00:00:00+00:00\"");
        await File.WriteAllTextAsync(path, edited);
        var (restarted, restartedStore, _) = Build(directory);

        Assert.True(await restarted.RollbackExpiredSwitchAsync(CancellationToken.None));
        var identity = await restartedStore.GetAsync(CancellationToken.None);
        Assert.False(identity.HasCredentials);
        Assert.Contains("à temps", identity.SwitchFailure);
    }

    [Fact]
    public async Task ForgettingAManualAddressRemovesItForGood()
    {
        var directory = NewDirectory();
        var (_, _, discovery) = Build(directory);
        var (listener, url, id, _) = Cluster(HttpStatusCode.OK);
        using var _ = listener;
        await discovery.AddByAddressAsync(url, CancellationToken.None);

        Assert.True(discovery.Forget(id));
        Assert.Empty(discovery.List());
        Assert.False(discovery.Forget(id));
        Assert.Equal("[]", (await File.ReadAllTextAsync(Path.Combine(directory, "known-clusters.json"))).Trim());
    }
}
