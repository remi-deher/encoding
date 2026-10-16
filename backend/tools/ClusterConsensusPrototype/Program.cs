using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

if (args.Length > 0 && args[0] == "node")
{
    await RunNodeAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "test")
{
    await RunScenarioAsync();
    return;
}

Console.WriteLine("DotNext Raft durable consensus prototype\n  dotnet run -- test");

async Task RunNodeAsync(string[] arguments)
{
    var id = arguments[1];
    var apiPort = int.Parse(arguments[2]);
    var raftPort = int.Parse(arguments[3]);
    var dataPath = Path.GetFullPath(arguments[4]);
    Directory.CreateDirectory(dataPath);

    var config = new RaftCluster.TcpConfiguration(new IPEndPoint(IPAddress.Loopback, raftPort))
    {
        ConfigurationStorage = null,
        LowerElectionTimeout = 600,
        UpperElectionTimeout = 1200,
        ColdStart = false,
    };
    var initialMembers = (InMemoryClusterConfigurationStorage<EndPoint>)config.ConfigurationStorage!;
    var memberBuilder = initialMembers.CreateInitialConfigurationBuilder();
    foreach (var port in new[] { 6521, 6522, 6523 })
        memberBuilder.Add(new IPEndPoint(IPAddress.Loopback, port));
    memberBuilder.Build();

    await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = dataPath }, IStateMachine.CreateNoOp());
    using var cluster = new RaftCluster(config) { AuditTrail = wal };
    await cluster.StartAsync(CancellationToken.None);

    var host = WebApplication.CreateBuilder();
    host.WebHost.UseUrls($"http://127.0.0.1:{apiPort}");
    var app = host.Build();
    app.MapGet("/state", () => Results.Ok(new NodeStatus(id,
        cluster.Leader?.EndPoint.Equals(new IPEndPoint(IPAddress.Loopback, raftPort)) == true,
        cluster.Leader?.EndPoint.ToString(), wal.LastEntryIndex, wal.LastCommittedEntryIndex)));
    app.MapPost("/write", async (HttpRequest request) =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(timeout.Token);
            await cluster.ReplicateAsync(Encoding.UTF8.GetBytes(body), timeout.Token);
            return Results.Ok(new { accepted = true, wal.LastCommittedEntryIndex });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Write rejected on {id}: {exception.GetType().Name}");
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: exception.GetType().Name);
        }
    });

    app.Lifetime.ApplicationStopping.Register(() => cluster.StopAsync(CancellationToken.None).GetAwaiter().GetResult());
    Console.WriteLine($"READY {id} http={apiPort} raft={raftPort} committed={wal.LastCommittedEntryIndex}");
    await app.RunAsync();
}

async Task RunScenarioAsync()
{
    var assemblyPath = Path.Combine(AppContext.BaseDirectory, "ClusterConsensusPrototype.dll");
    var root = Path.Combine(Path.GetTempPath(), "encoding-raft-three-node-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var ids = new[] { "raft-a", "raft-b", "raft-c" };
    var httpPorts = new[] { 6511, 6512, 6513 };
    var raftPorts = new[] { 6521, 6522, 6523 };
    var processes = new Process?[3];
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

    Process StartNode(int index)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { assemblyPath, "node", ids[index], httpPorts[index].ToString(), raftPorts[index].ToString(), Path.Combine(root, ids[index]) },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {ids[index]}");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    try
    {
        for (var i = 0; i < 3; i++) processes[i] = StartNode(i);
        await WaitUntilAsync(async () =>
        {
            var states = await ReadStatesAsync(client, httpPorts);
            return states.Length == 3 && states.Any(s => s.IsLeader);
        }, TimeSpan.FromSeconds(25), "initial election");

        var leader = (await ReadStatesAsync(client, httpPorts)).Single(s => s.IsLeader);
        var leaderIndex = Array.IndexOf(ids, leader.NodeId);
        using (var response = await client.PostAsync($"http://127.0.0.1:{httpPorts[leaderIndex]}/write", new StringContent("{\"id\":\"first\",\"value\":42}", Encoding.UTF8, "application/json")))
            Ensure(response.IsSuccessStatusCode, "leader commits a write");
        await WaitUntilAsync(async () => (await ReadStatesAsync(client, httpPorts)).All(s => s.CommittedIndex >= 1),
            TimeSpan.FromSeconds(15), "first write replicated to all nodes");

        var followerIndexes = Enumerable.Range(0, 3).Where(i => i != leaderIndex).ToArray();
        foreach (var follower in followerIndexes) StopNode(processes, follower);
        using (var response = await client.PostAsync($"http://127.0.0.1:{httpPorts[leaderIndex]}/write", new StringContent("{\"id\":\"no-quorum\",\"value\":7}", Encoding.UTF8, "application/json")))
            Ensure(response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable, "write is rejected after quorum loss");

        processes[followerIndexes[0]] = StartNode(followerIndexes[0]);
        await WaitUntilAsync(async () => (await ReadStatesAsync(client, httpPorts)).Count(s => s.CommittedIndex >= 1) >= 2,
            TimeSpan.FromSeconds(20), "quorum restored with one follower");
        processes[followerIndexes[1]] = StartNode(followerIndexes[1]);
        await WaitUntilAsync(async () => (await ReadStatesAsync(client, httpPorts)).Length == 3,
            TimeSpan.FromSeconds(20), "third node returns");
        StopNode(processes, leaderIndex);
        processes[leaderIndex] = null;

        try
        {
            await WaitUntilAsync(async () =>
            {
                var statuses = await ReadStatesAsync(client, httpPorts);
                return statuses.Length == 2 && statuses.Any(s => s.IsLeader);
            }, TimeSpan.FromSeconds(20), "leadership after former leader loss");
        }
        catch
        {
            Console.WriteLine("Status after leader loss: " + JsonSerializer.Serialize(await ReadStatesAsync(client, httpPorts), json));
            throw;
        }
        var newLeader = (await ReadStatesAsync(client, httpPorts)).Single(s => s.IsLeader);
        var newLeaderIndex = Array.IndexOf(ids, newLeader.NodeId);
        using (var response = await client.PostAsync($"http://127.0.0.1:{httpPorts[newLeaderIndex]}/write", new StringContent("{\"id\":\"after-recovery\",\"value\":99}", Encoding.UTF8, "application/json")))
            Ensure(response.IsSuccessStatusCode, "remaining quorum commits after leader loss");

        processes[leaderIndex] = StartNode(leaderIndex);
        await WaitUntilAsync(async () => (await ReadStatesAsync(client, httpPorts)).All(s => s.CommittedIndex >= 2),
            TimeSpan.FromSeconds(25), "restarted node replays its WAL and catches up");

        for (var i = 0; i < 3; i++) StopNode(processes, i);
        processes[0] = StartNode(0);
        await WaitUntilAsync(async () => (await ReadStatesAsync(client, httpPorts)).Length == 1,
            TimeSpan.FromSeconds(15), "single node returns after a full cluster stop");
        var recoveredAlone = (await ReadStatesAsync(client, httpPorts)).Single();
        Ensure(recoveredAlone.CommittedIndex >= 2, "committed log survives isolated process restart");

        StopNode(processes, 0);
        for (var i = 0; i < 3; i++) processes[i] = StartNode(i);
        await WaitUntilAsync(async () => (await ReadStatesAsync(client, httpPorts)).All(s => s.CommittedIndex >= 2),
            TimeSpan.FromSeconds(25), "all nodes recover after full cluster restart");
        Console.WriteLine("PASS: DotNext 6.10.0, three nodes, persistent WAL, election, quorum rejection, re-election, isolated restart and catch-up.");
    }
    finally
    {
        for (var i = 0; i < processes.Length; i++) StopNode(processes, i);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

static void StopNode(Process?[] processes, int index)
{
    var process = processes[index];
    if (process is null) return;
    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    process.Dispose();
    processes[index] = null;
}

async Task<NodeStatus[]> ReadStatesAsync(HttpClient client, int[] ports)
{
    var states = new List<NodeStatus>();
    foreach (var port in ports)
    {
        try
        {
            var status = await client.GetFromJsonAsync<NodeStatus>($"http://127.0.0.1:{port}/state", json);
            if (status is not null) states.Add(status);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
    }
    return states.ToArray();
}

async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        if (await condition()) return;
        await Task.Delay(150);
    }
    throw new TimeoutException($"Timed out waiting for {description}");
}

static void Ensure(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
}

record NodeStatus(string NodeId, bool IsLeader, string? LeaderId, long LastEntryIndex, long CommittedIndex);
