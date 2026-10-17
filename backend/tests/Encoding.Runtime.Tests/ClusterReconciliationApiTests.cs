using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Encoding.Contracts.Cluster;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterReconciliationApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SignedExchangeAppliesAndReturnsEventsIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-mesh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.db");
        var targetPath = Path.Combine(root, "target.db");
        var sourceConnectionString = $"Data Source={sourcePath};Pooling=False";
        var targetConnectionString = $"Data Source={targetPath};Pooling=False";
        try
        {
            await using var source = new SqliteDataSource(sourceConnectionString);
            await using var target = new SqliteDataSource(targetConnectionString);
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(source, migrations, CancellationToken.None);
            await DatabaseMigrator.ApplyAsync(target, migrations, CancellationToken.None);
            var clusterId = Guid.NewGuid();
            await new ClusterIdentityRepository(source).AdoptAsync(clusterId, "Media", CancellationToken.None);
            await new ClusterIdentityRepository(target).AdoptAsync(clusterId, "Media", CancellationToken.None);
            var sourceProtection = new EphemeralDataProtectionProvider();
            var sourceConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = Path.Combine(root, "source-keys")
            }).Build();
            using var sourceIdentity = new ClusterPeerIdentityStore(sourceConfiguration, sourceProtection);
            await TrustPeerAsync(target, clusterId, sourceIdentity);
            var sourceJournal = new ClusterReconciliationRepository(source);
            await sourceJournal.SetRegisterAsync("cluster.name", JsonSerializer.Serialize("Maison"), CancellationToken.None);
            var changes = await sourceJournal.GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
            var request = new ClusterReconciliationExchangeRequest(1, clusterId, new Dictionary<Guid, long>(), changes);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

            await using var factory = CreateFactory(targetConnectionString, root);
            using var client = factory.CreateClient();
            using var response = await SendSignedAsync(client, requestBytes, sourceIdentity);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            var responsePeerId = Guid.Parse(response.Headers.GetValues(ClusterReconciliationSignature.PeerHeader).Single());
            var targetIdentity = factory.Services.GetRequiredService<ClusterPeerIdentityStore>();
            Assert.Equal(targetIdentity.PeerId, responsePeerId);
            Assert.True(ClusterReconciliationSignature.Verify(
                targetIdentity.PublicKey,
                responsePeerId,
                response.Headers.GetValues(ClusterReconciliationSignature.TimeHeader).Single(),
                response.Headers.GetValues(ClusterReconciliationSignature.SignatureHeader).Single(),
                responseBytes));
            Assert.Equal("Maison", await ClusterNameAsync(target));
            Assert.Equal(1L, await CountEventsAsync(target));

            using var duplicate = await SendSignedAsync(client, requestBytes, sourceIdentity);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            Assert.Equal(1L, await CountEventsAsync(target));

            var rogueConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = Path.Combine(root, "rogue-keys")
            }).Build();
            using var rogueIdentity = new ClusterPeerIdentityStore(rogueConfiguration, sourceProtection);
            using var forged = await SendSignedAsync(client, requestBytes, rogueIdentity);
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExchangeRegistersAdvertisedPeerAndExchangesKnownPeers()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-mesh-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var targetPath = Path.Combine(root, "target.db");
        var targetConnectionString = $"Data Source={targetPath};Pooling=False";
        try
        {
            await using var target = new SqliteDataSource(targetConnectionString);
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(target, migrations, CancellationToken.None);
            var clusterId = Guid.NewGuid();
            await new ClusterIdentityRepository(target).AdoptAsync(clusterId, "Media", CancellationToken.None);
            var sourceProtection = new EphemeralDataProtectionProvider();
            var sourceConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = Path.Combine(root, "source-keys")
            }).Build();
            using var sourceIdentity = new ClusterPeerIdentityStore(sourceConfiguration, sourceProtection);
            await TrustPeerAsync(target, clusterId, sourceIdentity);

            var request = new ClusterReconciliationExchangeRequest(
                1,
                clusterId,
                new Dictionary<Guid, long>(),
                [],
                AdvertisedUrl: "http://peer-source:5080/",
                KnownPeers: ["http://peer-gossip-1:5080/"]);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

            await using var factory = CreateFactory(targetConnectionString, root);
            using var client = factory.CreateClient();
            using var response = await SendSignedAsync(client, requestBytes, sourceIdentity);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var repo = new ClusterReconciliationRepository(target);
            var endpoints = await repo.GetPeerEndpointsAsync(CancellationToken.None);
            Assert.Contains("http://peer-source:5080/", endpoints);
            Assert.Contains("http://peer-gossip-1:5080/", endpoints);

            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            var exchange = JsonSerializer.Deserialize<ClusterReconciliationExchangeResponse>(responseBytes, JsonOptions);
            Assert.NotNull(exchange);
            Assert.Contains("http://peer-source:5080/", exchange.KnownPeers ?? []);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WebApplicationFactory<AuthOptions> CreateFactory(string connectionString, string dataDirectory) =>
        new WebApplicationFactory<AuthOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:Cluster", connectionString);
            builder.UseSetting("Database:Provider", "sqlite");
            builder.UseSetting("Auth:SetupToken", "mesh-test-setup-token");
            builder.UseSetting("FlowBackups:Directory", Path.Combine(dataDirectory, "backups"));
            builder.UseSetting("Runtime:ArtifactDirectory", Path.Combine(dataDirectory, "artifacts"));
            builder.UseSetting("PluginSettings:KeyDirectory", Path.Combine(dataDirectory, "keys"));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
            });
        });

    private static async Task TrustPeerAsync(DbDataSource database, Guid clusterId, ClusterPeerIdentityStore peer)
    {
        await using var command = database.CreateCommand("""
            INSERT INTO cluster_peer_admissions (cluster_id, peer_id, public_key, approved_by_peer_id, wrapped_data_key, key_fingerprint, issued_at, signature)
            VALUES (@cluster_id, @peer_id, @public_key, @approved_by_peer_id, @wrapped_data_key, @key_fingerprint, @issued_at, @signature)
            """);
        command.Parameters.AddWithValue("cluster_id", clusterId);
        command.Parameters.AddWithValue("peer_id", peer.PeerId);
        command.Parameters.AddWithValue("public_key", peer.PublicKey);
        command.Parameters.AddWithValue("approved_by_peer_id", peer.PeerId);
        command.Parameters.AddWithValue("wrapped_data_key", "test-envelope");
        command.Parameters.AddWithValue("key_fingerprint", new string('0', 64));
        command.Parameters.AddWithValue("issued_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("signature", "test-signature");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HttpResponseMessage> SendSignedAsync(HttpClient client, byte[] body, ClusterPeerIdentityStore identity)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cluster/reconciliation/exchange")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(ClusterReconciliationSignature.PeerHeader, identity.PeerId.ToString("D"));
        request.Headers.Add(ClusterReconciliationSignature.TimeHeader, timestamp);
        request.Headers.Add(ClusterReconciliationSignature.SignatureHeader, ClusterReconciliationSignature.Create(identity, timestamp, body));
        return await client.SendAsync(request);
    }

    private static async Task<string> ClusterNameAsync(DbDataSource database)
    {
        await using var command = database.CreateCommand("SELECT name FROM cluster_identity WHERE singleton = true");
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountEventsAsync(DbDataSource database)
    {
        await using var command = database.CreateCommand("SELECT count(*) FROM cluster_change_log");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
