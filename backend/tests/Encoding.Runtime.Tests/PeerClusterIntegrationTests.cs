using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Encoding.Api.Infrastructure.PeerCluster;
using Encoding.Contracts.Libraries;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Xunit;

namespace Encoding.Runtime.Tests;

[Collection("peer-operations")]
public sealed class PeerClusterIntegrationTests
{
    [Fact]
    public async Task ThreeIndependentApisMutuallyAuthenticateAndConvergeClusterState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-peer-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var clusterId = Guid.NewGuid();
        var ports = Enumerable.Range(0, 6).Select(_ => ReservePort()).Distinct().ToArray();
        if (ports.Length != 6) throw new InvalidOperationException("Could not allocate six unique test ports.");
        await using var network = new PeerTestNetwork(ports[3..]);
        var processes = new PeerProcess[3];
        var clients = new HttpClient[3];
        var fixture = await SeedRuntimeFixtureAsync(Path.Combine(directory, "peer-0"));
        try
        {
            for (var index = 0; index < processes.Length; index++)
            {
                var nodeDirectory = Path.Combine(directory, $"peer-{index}");
                Directory.CreateDirectory(nodeDirectory);
                processes[index] = StartApi(index, nodeDirectory, clusterId, ports[index], ports[index + 3], bootstrap: index == 0, network.ProxyPort(index));
                clients[index] = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{ports[index]}/"),
                    Timeout = TimeSpan.FromSeconds(15)
                };
            }

            for (var index = 0; index < clients.Length; index++)
            {
                await WaitUntilAsync(async () => (await clients[index].GetAsync("health/live")).IsSuccessStatusCode,
                    TimeSpan.FromSeconds(35), $"API {index} startup", processes);
                using var setup = await clients[index].PostAsJsonAsync("api/v1/auth/setup",
                    new { username = "peeradmin", password = "StrongPassword123!", setupToken = (string?)null, nodeDisplayName = $"peer-{index}" });
                Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
            }

            var identities = new PeerIdentityResponse[3];
            for (var index = 0; index < clients.Length; index++)
                identities[index] = (await clients[index].GetFromJsonAsync<PeerIdentityResponse>("api/v1/peer-cluster/identity"))!;

            // Pin every peer on every API. Discovery and a matching cluster ID alone never grant trust.
            for (var receiver = 0; receiver < clients.Length; receiver++)
            for (var origin = 0; origin < clients.Length; origin++)
            {
                if (receiver == origin) continue;
                var identity = identities[origin];
                using var response = await SendAdminAsync(clients[receiver], HttpMethod.Post, "api/v1/peer-cluster/peers",
                    new TrustPeerRequest(identity.PeerId, identity.ClusterId, identity.Endpoint.ToString(), identity.CertificatePem, identity.Thumbprint));
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            }

            using (var create = await SendAdminAsync(clients[0], HttpMethod.Post, "api/v1/cluster", new { name = "Maison de test" }))
                Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            await WaitUntilAsync(async () =>
            {
                var current = await clients[0].GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus");
                return current is { IsLeader: true, HasQuorum: true, Members.Count: 1 };
            }, TimeSpan.FromSeconds(15), "single-node bootstrap election", processes);

            for (var index = 1; index < clients.Length; index++)
            {
                using var add = await SendAdminAsync(clients[0], HttpMethod.Post,
                    $"api/v1/peer-cluster/peers/{identities[index].PeerId:D}/membership", new { });
                Assert.True(add.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.Conflict,
                    $"Membership request returned {(int)add.StatusCode}: {await add.Content.ReadAsStringAsync()}\n{RelevantLogs(processes)}");
                if (add.StatusCode == HttpStatusCode.Conflict)
                {
                    // A follower may briefly lead during a joint configuration; retry after the new membership settles.
                    await WaitUntilAsync(async () =>
                    {
                        var status = await clients[0].GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus");
                        return status is { IsLeader: true };
                    }, TimeSpan.FromSeconds(15), "leadership before retrying membership", processes);
                    using var retry = await SendAdminAsync(clients[0], HttpMethod.Post,
                        $"api/v1/peer-cluster/peers/{identities[index].PeerId:D}/membership", new { });
                    Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
                }
            }

            await WaitUntilAsync(async () =>
            {
                var statuses = await Task.WhenAll(clients.Select(client => client.GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus")));
                // A lease is held only by the leader; followers report no local lease while sharing the committed membership.
                return statuses.All(status => status is { Members.Count: 3 })
                    && statuses.Any(status => status is { IsLeader: true, HasQuorum: true });
            }, TimeSpan.FromSeconds(40), "three-voter quorum", processes);

            await WaitUntilAsync(async () =>
            {
                var views = await Task.WhenAll(clients.Select(client => client.GetFromJsonAsync<PeerClusterView>("api/v1/peer-cluster")));
                return views.All(view => view is { Peers.Count: 2 }
                    && view.Peers.All(peer => peer.Status == "online"));
            }, TimeSpan.FromSeconds(40), "signed mTLS presence convergence", processes);

            foreach (var client in clients)
            {
                var cluster = await client.GetFromJsonAsync<ClusterView>("api/v1/cluster");
                Assert.NotNull(cluster);
                Assert.Equal(clusterId, cluster.Id);
                Assert.Equal("Maison de test", cluster.Name);
                Assert.False(cluster.Standalone);
            }

            await ExerciseProductionPathsAsync(directory, clusterId, ports, clients, processes, identities, network, fixture);
            var stableStatuses = await Task.WhenAll(clients.Select(client => client.GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus")));
            var leaderIndex = Array.FindIndex(stableStatuses, status => status is { IsLeader: true, HasQuorum: true });
            Assert.InRange(leaderIndex, 0, 2);
            var removedIndex = leaderIndex == 2 ? 1 : 2;
            var removedPeer = identities[removedIndex];
            using (var removal = await SendAdminAsync(clients[leaderIndex], HttpMethod.Delete,
                $"api/v1/peer-cluster/peers/{removedPeer.PeerId:D}", new { }))
                Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);

            await WaitUntilAsync(async () =>
            {
                var activeStatuses = await Task.WhenAll(Enumerable.Range(0, clients.Length).Where(index => index != removedIndex)
                    .Select(index => clients[index].GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus")));
                return activeStatuses.All(status => status is { Members.Count: 2 })
                    && activeStatuses.Any(status => status is { IsLeader: true, HasQuorum: true });
            }, TimeSpan.FromSeconds(20), "two-member quorum after removal", processes);
            var survivorIndices = Enumerable.Range(0, clients.Length).Where(index => index != removedIndex).ToArray();
            var survivorStatuses = await Task.WhenAll(survivorIndices.Select(index => clients[index].GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus")));
            var committedMembers = survivorStatuses.First(status => status is { IsLeader: true, HasQuorum: true })!.Members;
            Assert.DoesNotContain(removedPeer.Endpoint.ToString(), committedMembers);
            foreach (var status in survivorStatuses) Assert.Equal(committedMembers.Order(), status?.Members.Order());
            await WaitUntilAsync(async () =>
            {
                var views = await Task.WhenAll(clients.Select(client => client.GetFromJsonAsync<PeerClusterView>("api/v1/peer-cluster")));
                return views.Select((view, index) => (view, index)).Where(item => item.index != removedIndex)
                    .All(item => item.view is { Peers.Count: 1 }
                        && item.view.Peers.All(peer => peer.PeerId != removedPeer.PeerId));
            }, TimeSpan.FromSeconds(15), "cluster-wide peer revocation", processes);

            using var unauthenticated = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
            var rejectedWithoutCertificate = false;
            try
            {
                using var peerRequest = await unauthenticated.GetAsync(identities[0].Endpoint + "api/v1/peer-cluster/consensus");
                rejectedWithoutCertificate = !peerRequest.IsSuccessStatusCode;
            }
            catch (HttpRequestException) { rejectedWithoutCertificate = true; }
            Assert.True(rejectedWithoutCertificate, "The peer TLS port must reject clients without a pinned certificate.");
            using (var publicPresence = await clients[0].PostAsJsonAsync("api/v1/peer-cluster/presence", new { records = Array.Empty<object>() }))
                Assert.Equal(HttpStatusCode.NotFound, publicPresence.StatusCode);

            var trustedClientIndex = Enumerable.Range(0, clients.Length).First(index => index != 0 && index != removedIndex);
            using var peerCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(directory, $"peer-{trustedClientIndex}", "peer-data", "peer-identity.pfx"),
                TestIdentityPassword(trustedClientIndex), X509KeyStorageFlags.EphemeralKeySet);
            using var trustedPeerClient = new HttpClient(new HttpClientHandler
            {
                ClientCertificates = { peerCertificate },
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
            using var leakedApi = await trustedPeerClient.GetAsync(identities[0].Endpoint + "health/live");
            Assert.Equal(HttpStatusCode.NotFound, leakedApi.StatusCode);
            using (var incompatible = new HttpRequestMessage(HttpMethod.Get, new Uri(identities[0].Endpoint, PeerRequestRouting.StatusPath)))
            {
                incompatible.Headers.Add(PeerRequestRouting.ProtocolHeader, "999");
                incompatible.Headers.Add(PeerRequestRouting.SchemaHeader, "unsupported");
                using var rejected = await trustedPeerClient.SendAsync(incompatible);
                Assert.Equal(HttpStatusCode.UpgradeRequired, rejected.StatusCode);
            }

            survivorStatuses = await Task.WhenAll(survivorIndices.Select(index => clients[index].GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus")));
            var leaderPosition = Array.FindIndex(survivorStatuses, status => status is { IsLeader: true, HasQuorum: true });
            Assert.InRange(leaderPosition, 0, survivorStatuses.Length - 1);
            var leaderStatus = survivorStatuses[leaderPosition]!;
            var followerEndpoint = leaderStatus.Members.Single(endpoint => endpoint != leaderStatus.LeaderEndpoint);
            var stoppedIndex = Array.FindIndex(identities, identity => identity.Endpoint.ToString() == followerEndpoint);
            Assert.Contains(stoppedIndex, survivorIndices);
            var survivingLeaderIndex = survivorIndices[leaderPosition];
            processes[stoppedIndex].Process.Kill(entireProcessTree: true);
            await WaitForQuorumLossAsync(clients[survivingLeaderIndex], TimeSpan.FromSeconds(15));
            using (var withoutQuorum = await SendAdminAsync(clients[survivingLeaderIndex], HttpMethod.Put,
                "api/v1/cluster", new { name = "Sans quorum" }))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, withoutQuorum.StatusCode);
            var unchangedCluster = await clients[survivingLeaderIndex].GetFromJsonAsync<ClusterView>("api/v1/cluster");
            Assert.Equal("Maison de test", unchangedCluster?.Name);
        }
        finally
        {
            foreach (var client in clients) client?.Dispose();
            foreach (var process in processes)
            {
                if (process is null) continue;
                try { if (!process.Process.HasExited) process.Process.Kill(entireProcessTree: true); } catch { }
                try { await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                process.Process.Dispose();
            }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private sealed record RuntimeFixture(Guid Worker, Guid Execution, string Token);

    private static async Task<RuntimeFixture> SeedRuntimeFixtureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await using var db = new SqliteDataSource($"Data Source={Path.Combine(directory, "cluster.sqlite")};Pooling=False");
        await DatabaseMigrator.ApplyAsync(db, Harness.RepositoryRoot(), CancellationToken.None);
        var (registry, _, _) = Harness.Services();
        var plugin = registry.Plugins.Single(plugin => plugin.Components.Any(component => component.Id == "base-input-file"));
        var entry = plugin.Components.Single(component => component.Id == "base-input-file");
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Peer lease fixture", "video",
            [new("entry", null, new(plugin.Id, plugin.Version, entry.Id, entry.Version, entry.ConfigurationSchema.Version),
                entry.ConfigurationSchema.Fields.Where(field => field.DefaultValue is not null).ToDictionary(field => field.Id, field => field.DefaultValue!.Value))], []);
        var fixture = new RuntimeFixture(Guid.NewGuid(), Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var library = Guid.NewGuid();
        await using var connection = await db.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var command = Sql.Command("""
            INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,node_token_hash)
            VALUES(@id,'fixture worker',@roles,2,@caps,clock_timestamp(),@token);
            """, connection, tx))
        {
            command.Parameters.AddWithValue("id", fixture.Worker);
            command.Parameters.AddWithValue("roles", SqlKind.TextArray, new[] { "worker" });
            command.Parameters.AddWithValue("caps", SqlKind.Json, JsonSerializer.Serialize(new NodeToolCapability[]
                { new("plugin/" + plugin.Id, plugin.Version, ["general"], []), new("ffprobe", "7", ["video"], []) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("token", SqlKind.Bytes, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fixture.Token)));
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = Sql.Command("""
            INSERT INTO libraries(id,name) VALUES(@library,'Lease fixture');
            INSERT INTO library_node_accesses(library_id,node_id,mode,is_enabled,local_path,can_read,can_write) VALUES(@library,@node,'direct',true,'/synthetic-test-library',true,true)
            """, connection, tx))
        {
            command.Parameters.AddWithValue("library", library); command.Parameters.AddWithValue("node", fixture.Worker);
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = Sql.Command("""
            INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES(@id,'synthetic.mkv','Lease fixture','Peer lease fixture',1,@flow,'queued',1);
            INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES(@id,@library,'synthetic.mkv','SYNTHETIC.MKV');
            """, connection, tx))
        {
            command.Parameters.AddWithValue("id", fixture.Execution); command.Parameters.AddWithValue("library", library);
            command.Parameters.AddWithValue("flow", SqlKind.Json, JsonSerializer.Serialize(flow, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await command.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return fixture;
    }

    private static async Task ExerciseProductionPathsAsync(string root, Guid clusterId, int[] ports, HttpClient[] clients,
        PeerProcess[] processes, PeerIdentityResponse[] identities, PeerTestNetwork network, RuntimeFixture fixture)
    {
        async Task<int> LeaderAsync()
        {
            var states = await Task.WhenAll(clients.Select(client => client.GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus")));
            return Array.FindIndex(states, state => state is { IsLeader: true, HasQuorum: true });
        }
        async Task StopAsync(int index)
        {
            var peer = processes[index]; processes[index] = null!;
            peer.Process.Kill(entireProcessTree: true);
            await peer.Process.WaitForExitAsync();
            peer.Process.Dispose();
        }
        void Restart(int index) => processes[index] = StartApi(index, Path.Combine(root, $"peer-{index}"), clusterId,
            ports[index], ports[index + 3], false, network.ProxyPort(index));
        async Task ThreeReadyAsync()
        {
            await WaitUntilAsync(async () =>
            {
                var states = await Task.WhenAll(clients.Select(client => client.GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus")));
                return states.All(state => state is { Members.Count: 3 }) && states.Count(state => state is { IsLeader: true, HasQuorum: true }) == 1;
            }, TimeSpan.FromSeconds(40), "recovered three-voter membership", processes);
        }
        var leader = await LeaderAsync();
        var follower = (leader + 1) % 3;
        await WaitUntilAsync(async () => (await Task.WhenAll(clients.Select(client => client.GetAsync("health/ready"))))
            .All(response => response.IsSuccessStatusCode), TimeSpan.FromSeconds(30), "all SQL projections ready", processes);
        using (var create = await SendAdminAsync(clients[follower], HttpMethod.Post, "api/v1/libraries", new { name = "Replicated configuration" }))
            Assert.True(create.StatusCode == HttpStatusCode.Created, $"{create.StatusCode}: {await create.Content.ReadAsStringAsync()}\n{RelevantLogs(processes)}");
        using (var create = await SendAdminAsync(clients[follower], HttpMethod.Post, "api/v1/scripts",
            new CreateFlowScriptRequest(new("Replicated script", "javascript", "test", "return 'ok';"))))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var script = (await create.Content.ReadFromJsonAsync<FlowScriptDetails>())!;
            using var publish = await SendAdminAsync(clients[follower], HttpMethod.Post, $"api/v1/scripts/{script.Script.Id:D}/publish", new PublishFlowScriptRequest(1));
            Assert.Equal(HttpStatusCode.Created, publish.StatusCode);
        }
        using (var create = await SendAdminAsync(clients[follower], HttpMethod.Post, "api/v1/encoding-profiles",
            new CreateEncodingProfileRequest(new("Peer profile", "test", BuiltInEncodingProfiles.Balanced.Targets))))
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        async Task<bool> ConvergedAsync()
        {
            foreach (var client in clients)
            {
                if (!(await client.GetFromJsonAsync<LibraryResponse[]>("api/v1/libraries"))!.Any(library => library.Name == "Replicated configuration")) return false;
                if (!(await client.GetFromJsonAsync<FlowScriptSummary[]>("api/v1/scripts"))!.Any(script => script.DraftName == "Replicated script" && script.PublishedVersion == 1)) return false;
            }
            return true;
        }
        await WaitUntilAsync(ConvergedAsync, TimeSpan.FromSeconds(20), "SQL transaction projections on all peers", processes);
        RuntimeLease lease;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/runtime/nodes/{fixture.Worker:D}/claim"))
        {
            request.Headers.Add("X-Node-Token", fixture.Token);
            using var claim = await clients[follower].SendAsync(request);
            if (!claim.IsSuccessStatusCode) await Task.Delay(100);
            Assert.True(claim.StatusCode == HttpStatusCode.OK, $"{claim.StatusCode}: {await claim.Content.ReadAsStringAsync()}\n{RelevantLogs(processes)}");
            lease = (await claim.Content.ReadFromJsonAsync<RuntimeLease>())!;
            Assert.Equal(fixture.Execution, lease.ExecutionId);
        }
        network.Isolate(leader);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var index = await LeaderAsync();
                return index >= 0 && index != leader && (await clients[leader].GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus")) is { HasQuorum: false };
            }, TimeSpan.FromSeconds(20), "majority election during bidirectional leader partition", processes);
            using (var blocked = await SendAdminAsync(clients[leader], HttpMethod.Post, "api/v1/libraries", new { name = "Must never commit" }))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            var majorityLeader = await LeaderAsync();
            using (var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/runtime/nodes/{fixture.Worker:D}/executions/{fixture.Execution:D}/renew")
            { Content = JsonContent.Create(new RuntimeFence(lease.Epoch)) })
            {
                request.Headers.Add("X-Node-Token", fixture.Token);
                using var stale = await clients[majorityLeader].SendAsync(request);
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            }
            using var accepted = await SendAdminAsync(clients[majorityLeader], HttpMethod.Post, "api/v1/libraries", new { name = "Majority survives" });
            if (!accepted.IsSuccessStatusCode) await Task.Delay(100);
            Assert.True(accepted.StatusCode == HttpStatusCode.Created, $"{accepted.StatusCode}: {await accepted.Content.ReadAsStringAsync()}\n{RelevantLogs(processes)}");
        }
        finally { network.Reconnect(leader); }
        await ThreeReadyAsync();
        await WaitUntilAsync(async () => (await clients[leader].GetFromJsonAsync<LibraryResponse[]>("api/v1/libraries"))!.Any(library => library.Name == "Majority survives"),
            TimeSpan.FromSeconds(20), "catch-up of the isolated leader", processes);
        foreach (var client in clients)
            Assert.DoesNotContain((await client.GetFromJsonAsync<LibraryResponse[]>("api/v1/libraries"))!, library => library.Name == "Must never commit");

        var rotating = ((await LeaderAsync()) + 1) % 3;
        PeerCertificateRequest prepared;
        using (var prepare = await SendAdminAsync(clients[rotating], HttpMethod.Post, "api/v1/peer-cluster/certificate/prepare", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
            var document = await prepare.Content.ReadFromJsonAsync<JsonElement>();
            prepared = new(document.GetProperty("certificatePem").GetString()!, document.GetProperty("thumbprint").GetString()!);
        }
        leader = await LeaderAsync();
        using (var approve = await SendAdminAsync(clients[leader], HttpMethod.Post,
            $"api/v1/peer-cluster/peers/{identities[rotating].PeerId:D}/certificate", prepared))
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var committed = (await clients[leader].GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus"))!.LastCommittedEntryIndex;
        await WaitUntilAsync(async () => (await Task.WhenAll(clients.Select(client => client.GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus"))))
            .All(state => state!.LastCommittedEntryIndex >= committed), TimeSpan.FromSeconds(20), "certificate trust overlap", processes);
        await StopAsync(rotating);
        using (var identity = PeerIdentityStore.Open(Path.Combine(root, $"peer-{rotating}", "peer-data"), clusterId, pfxPassword: TestIdentityPassword(rotating)))
            identity.ActivateRotation(TestIdentityPassword(rotating), prepared.Fingerprint);
        Restart(rotating);
        await ThreeReadyAsync();
        leader = await LeaderAsync();
        using (var complete = await SendAdminAsync(clients[leader], HttpMethod.Post,
            $"api/v1/peer-cluster/peers/{identities[rotating].PeerId:D}/certificate/complete", new { }))
            Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        await StopAsync(rotating);
        var nodeDirectory = Path.Combine(root, $"peer-{rotating}");
        var backup = Path.Combine(root, "cold-backup");
        var backupSet = Guid.NewGuid().ToString();
        var previousPassword = Environment.GetEnvironmentVariable("PeerCluster__IdentityPassword");
        Environment.SetEnvironmentVariable("PeerCluster__IdentityPassword", TestIdentityPassword(rotating));
        try
        {
            string[] common = ["--cluster-id", clusterId.ToString(), "--database", $"Data Source={Path.Combine(nodeDirectory, "cluster.sqlite")}",
                "--data-directory", Path.Combine(nodeDirectory, "peer-data"), "--key-directory", Path.Combine(nodeDirectory, "plugin-keys"), "--backup-set", backupSet];
            Assert.Equal(0, await PeerOperationsCommand.RunAsync("backup-peer", [.. common, "--output", backup], Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
            Directory.Delete(nodeDirectory, true); // Only the synthetic test peer; contains no user media.
            Assert.Equal(0, await PeerOperationsCommand.RunAsync("restore-peer", [.. common, "--input", backup], Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
        }
        finally { Environment.SetEnvironmentVariable("PeerCluster__IdentityPassword", previousPassword); }
        Restart(rotating);
        await ThreeReadyAsync();
        await WaitUntilAsync(ConvergedAsync, TimeSpan.FromSeconds(20), "cold restore and log catch-up", processes);
        using (var afterSnapshot = await SendAdminAsync(clients[await LeaderAsync()], HttpMethod.Post,
            "api/v1/libraries", new { name = "Must replay from WAL" }))
            Assert.Equal(HttpStatusCode.Created, afterSnapshot.StatusCode);
        for (var index = 0; index < 3; index++) await StopAsync(index);
        var coordinatedSet = Guid.NewGuid().ToString();
        previousPassword = Environment.GetEnvironmentVariable("PeerCluster__IdentityPassword");
        try
        {
            for (var index = 0; index < 3; index++)
            {
                Environment.SetEnvironmentVariable("PeerCluster__IdentityPassword", TestIdentityPassword(index));
                nodeDirectory = Path.Combine(root, $"peer-{index}");
                string[] common = ["--cluster-id", clusterId.ToString(), "--database", $"Data Source={Path.Combine(nodeDirectory, "cluster.sqlite")}",
                    "--data-directory", Path.Combine(nodeDirectory, "peer-data"), "--key-directory", Path.Combine(nodeDirectory, "plugin-keys"), "--backup-set", coordinatedSet];
                var archive = Path.Combine(root, $"coordinated-backup-{index}");
                Assert.Equal(0, await PeerOperationsCommand.RunAsync("backup-peer", [.. common, "--output", archive], Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
                Directory.Delete(nodeDirectory, true);
                Assert.Equal(0, await PeerOperationsCommand.RunAsync("restore-peer", [.. common, "--input", archive], Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
            }
        }
        finally { Environment.SetEnvironmentVariable("PeerCluster__IdentityPassword", previousPassword); }
        for (var index = 0; index < 3; index++) Restart(index);
        await ThreeReadyAsync();
        await WaitUntilAsync(ConvergedAsync, TimeSpan.FromSeconds(20), "complete cluster restart", processes);
        foreach (var client in clients)
        {
            Assert.Contains((await client.GetFromJsonAsync<LibraryResponse[]>("api/v1/libraries"))!, library => library.Name == "Must replay from WAL");
            var execution = await client.GetFromJsonAsync<ExecutionResponse>($"api/v1/executions/{fixture.Execution:D}");
            Assert.Equal("running", execution!.State);
            Assert.Equal(fixture.Worker, execution.NodeId);
            var metrics = await client.GetStringAsync("metrics");
            Assert.Contains("encoding_peer_projection_revision", metrics);
            Assert.Contains("encoding_peer_certificate_remaining_seconds", metrics);
        }
    }

    private static PeerProcess StartApi(int index, string directory, Guid clusterId, int apiPort, int peerPort, bool bootstrap, int? proxyPort = null)
    {
        var apiAssembly = typeof(PeerIdentityStore).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { apiAssembly },
            WorkingDirectory = Path.GetDirectoryName(apiAssembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{apiPort}";
        start.Environment["ConnectionStrings__Cluster"] = $"Data Source={Path.Combine(directory, "cluster.sqlite")}";
        start.Environment["Node__DataDirectory"] = Path.Combine(directory, "node-data");
        start.Environment["PluginSettings__KeyDirectory"] = Path.Combine(directory, "plugin-keys");
        start.Environment["PeerCluster__Enabled"] = "true";
        start.Environment["PeerCluster__ClusterId"] = clusterId.ToString("D");
        if (proxyPort is { } proxy) start.Environment["PeerCluster__TransportProxy"] = $"http://127.0.0.1:{proxy}/";
        start.Environment["PeerCluster__SharedDataKey"] = Convert.ToBase64String(SHA256.HashData(clusterId.ToByteArray())); // Synthetic test key only.
        start.Environment["PeerCluster__Bootstrap"] = bootstrap.ToString().ToLowerInvariant();
        start.Environment["PeerCluster__IdentityPassword"] = TestIdentityPassword(index);
        start.Environment["PeerCluster__PublicEndpoint"] = $"https://127.0.0.1:{peerPort}/";
        start.Environment["PeerCluster__ListenPort"] = peerPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PeerCluster__DataDirectory"] = Path.Combine(directory, "peer-data");
        start.Environment["Auth__CookieSecure"] = "false";
        start.Environment["NodePresence__OfflineAfterSeconds"] = "3600";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start API {index}.");
        var output = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new PeerProcess(process, output);
    }

    private static async Task<HttpResponseMessage> SendAdminAsync<T>(HttpClient client, HttpMethod method, string path, T payload)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-Encoding-Csrf", "1");
        return await client.SendAsync(request);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description, PeerProcess[] processes)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (processes.Any(process => process?.Process.HasExited == true))
                throw new InvalidOperationException($"An API process exited while waiting for {description}: {string.Join("\n", processes.SelectMany(process => process?.Output ?? []))}");
            try { if (await condition()) return; }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Timed out waiting for {description}: {string.Join("\n", processes.SelectMany(process => process?.Output ?? []))}");
    }

    private static async Task WaitForQuorumLossAsync(HttpClient client, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        PeerConsensusStatus? latest = null;
        while (timer.Elapsed < timeout)
        {
            try
            {
                latest = await client.GetFromJsonAsync<PeerConsensusStatus>("api/v1/peer-cluster/consensus");
                if (latest is { HasQuorum: false }) return;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Le pair survivant n’a pas signalé la perte du quorum Raft : {JsonSerializer.Serialize(latest)}");
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string TestIdentityPassword(int index) => Convert.ToBase64String(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"integration-peer-{index}")));

    private static string RelevantLogs(IEnumerable<PeerProcess> processes) => string.Join("\n",
        processes.SelectMany(process => process.Output)
            .Where(line => line.Contains("warn:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Raft membership", StringComparison.OrdinalIgnoreCase)
                || line.TrimStart().StartsWith("at ", StringComparison.Ordinal)
                || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error:", StringComparison.OrdinalIgnoreCase)));


    private sealed record PeerProcess(Process Process, ConcurrentQueue<string> Output);
    private sealed record PeerIdentityResponse(Guid ClusterId, Guid PeerId, string Thumbprint, string CertificatePem, Uri Endpoint);
    private sealed record PeerView(Guid PeerId, string Endpoint, string Thumbprint, string Status, DateTimeOffset? LastSeenAt, int NodeCount, int OnlineNodeCount);
    private sealed record PeerClusterView(Guid ClusterId, Guid LocalPeerId, string LocalThumbprint, string LocalCertificatePem,
        IReadOnlyList<PeerView> Peers, IReadOnlyList<object> Nodes, bool PresenceMayDiverge);
    private sealed record PeerConsensusStatus(bool IsLeader, string? LeaderEndpoint, IReadOnlyList<string> Members,
        long LastEntryIndex, long LastCommittedEntryIndex, bool HasQuorum);
    private sealed record ClusterView(Guid Id, string Name, bool Standalone);
}
