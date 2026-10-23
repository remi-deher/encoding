using System.Net;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Operations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterReconciliationServiceTests
{
    [Fact]
    public async Task TimeoutRecordsPeerFailureAndContinuesToTheNextPeerAndNextCycle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempts = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            attempts.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "slow.test"
                ? throw new TaskCanceledException("Simulated HttpClient timeout")
                : Task.FromResult(fixture.Response());
        }));
        using var service = fixture.Service(client);
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://slow.test/", "http://healthy.test/"], CancellationToken.None);
        Assert.Equal(["slow.test", "healthy.test"], attempts);
        var peers = fixture.Health.GetStatus().Peers;
        Assert.NotNull(peers.Single(peer => peer.Url == "http://slow.test/").LastError);
        Assert.NotNull(peers.Single(peer => peer.Url == "http://healthy.test/").LastSuccessAt);
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://healthy.test/"], CancellationToken.None);
        Assert.Equal(3, attempts.Count);
    }

    [Fact]
    public async Task ServiceCancellationStopsTheCycleWithoutRecordingAPeerFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            attempts++;
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }));
        using var service = fixture.Service(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReconcilePeersAsync(
            fixture.ClusterId, ["http://first.test/", "http://second.test/"], stop.Token));
        Assert.Equal(1, attempts);
        Assert.Null(Assert.Single(fixture.Health.GetStatus().Peers).LastError);
    }

    [Fact]
    public async Task UnexpectedServiceExitDisablesMeshAndDegradesDetailedHealth()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var remove = fixture.Database.Db.CreateCommand("DELETE FROM cluster_identity")) await remove.ExecuteNonQueryAsync();
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No request expected")));
        using var service = fixture.Service(client);
        fixture.Health.Enable();
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var status = fixture.Health.GetStatus();
        Assert.False(status.Enabled);
        Assert.Contains("arrêté", status.DisabledReason);
        var check = await new ClusterReconciliationHealthCheck(fixture.Health).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, check.Status);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string root = Path.Combine(Path.GetTempPath(), "encoding-mesh-service-" + Guid.NewGuid().ToString("N"));
        private readonly EphemeralDataProtectionProvider protection = new();
        private IConfiguration configuration = null!;
        private ClusterPeerIdentityStore identity = null!;
        private ClusterDataKeyStore keys = null!;
        private ClusterPeerAdmissionRepository admissions = null!;
        public TestDatabase Database { get; private set; } = null!;
        public ClusterReconciliationRepository Journal { get; private set; } = null!;
        public ClusterReconciliationHealth Health { get; } = new();
        public Guid ClusterId { get; } = Guid.NewGuid();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Database = await TestDatabase.CreateSqliteAsync("mesh-service");
            await DatabaseMigrator.ApplyAsync(fixture.Database.Db, Harness.RepositoryRoot(), CancellationToken.None);
            var clusters = new ClusterIdentityRepository(fixture.Database.Db);
            await clusters.AdoptAsync(fixture.ClusterId, "Test", CancellationToken.None);
            fixture.configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = fixture.root
            }).Build();
            fixture.identity = new(fixture.configuration, fixture.protection);
            fixture.keys = new(fixture.configuration, fixture.protection);
            fixture.admissions = new(fixture.Database.Db, clusters, fixture.identity, fixture.keys);
            await fixture.admissions.EnsureRootAdmissionAsync(fixture.ClusterId, CancellationToken.None);
            fixture.Journal = new(fixture.Database.Db);
            return fixture;
        }

        public ClusterReconciliationService Service(HttpClient client) => new(
            new(Database.Db), Journal,
            new(Database.Db, keys, protection, NullLogger<ClusterEntityReconciliation>.Instance),
            identity, admissions, keys, Health, configuration, new ClientFactory(client),
            NullLogger<ClusterReconciliationService>.Instance);

        public HttpResponseMessage Response(IReadOnlyDictionary<Guid, long>? vector = null, IReadOnlyList<ClusterChangeEvent>? changes = null)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new ClusterReconciliationExchangeResponse(
                1, ClusterId, vector ?? new Dictionary<Guid, long>(), changes ?? []), Json);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Headers.Add(ClusterReconciliationSignature.PeerHeader, identity.PeerId.ToString("D"));
            response.Headers.Add(ClusterReconciliationSignature.TimeHeader, timestamp);
            response.Headers.Add(ClusterReconciliationSignature.SignatureHeader, ClusterReconciliationSignature.Create(identity, timestamp, body));
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            identity.Dispose();
            keys.Dispose();
            await Database.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
