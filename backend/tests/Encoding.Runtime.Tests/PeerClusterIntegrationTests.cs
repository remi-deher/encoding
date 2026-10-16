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
using Xunit;

namespace Encoding.Runtime.Tests;

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
        var processes = new PeerProcess[3];
        var clients = new HttpClient[3];
        try
        {
            for (var index = 0; index < processes.Length; index++)
            {
                var nodeDirectory = Path.Combine(directory, $"peer-{index}");
                Directory.CreateDirectory(nodeDirectory);
                processes[index] = StartApi(index, nodeDirectory, clusterId, ports[index], ports[index + 3], bootstrap: index == 0);
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

    private static PeerProcess StartApi(int index, string directory, Guid clusterId, int apiPort, int peerPort, bool bootstrap)
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
        start.Environment["PeerCluster__Bootstrap"] = bootstrap.ToString().ToLowerInvariant();
        start.Environment["PeerCluster__IdentityPassword"] = TestIdentityPassword(index);
        start.Environment["PeerCluster__PublicEndpoint"] = $"https://127.0.0.1:{peerPort}/";
        start.Environment["PeerCluster__ListenPort"] = peerPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PeerCluster__DataDirectory"] = Path.Combine(directory, "peer-data");
        start.Environment["Auth__CookieSecure"] = "false";
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
