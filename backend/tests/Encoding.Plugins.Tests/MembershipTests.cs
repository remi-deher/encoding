using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Encoding.Core.Nodes;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Rattachement d'un nœud à un cluster (0039) : départ, serveur local arrêté à l'arrivée ailleurs, cluster injoignable.</summary>
internal static class MembershipTests
{
    public static async Task RunAsync()
    {
        var data = Path.Combine(Path.GetTempPath(), "encoding-membership-" + Guid.NewGuid().ToString("N"));
        var leaveStatus = HttpStatusCode.NoContent;
        var leaves = new List<(string Path, string Token)>();
        var mode = new NodeMode(true, true);

        var clusterBuilder = WebApplication.CreateBuilder();
        clusterBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cluster = clusterBuilder.Build();
        cluster.MapPost("/api/v1/nodes/{id}/leave", (string id, HttpRequest request) =>
        {
            leaves.Add((request.Path.Value!, request.Headers["X-Node-Token"].ToString()));
            return Results.StatusCode((int)leaveStatus);
        });
        await cluster.StartAsync();
        var clusterUrl = cluster.Urls.Single().TrimEnd('/') + "/";

        var launcherBuilder = WebApplication.CreateBuilder();
        launcherBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var launcherApp = launcherBuilder.Build();
        launcherApp.MapGet("/mode", () => Results.Ok(mode));
        launcherApp.MapPut("/mode", (NodeMode requested) => { mode = requested; return Results.Ok(mode); });
        await launcherApp.StartAsync();
        var launcher = new LauncherControlClient(new HttpClient { BaseAddress = new Uri(launcherApp.Urls.Single().TrimEnd('/') + "/") });

        try
        {
            Directory.CreateDirectory(data);
            var secretFile = Path.Combine(data, "secret");
            File.WriteAllText(secretFile, "secret");
            var localUrl = "http://127.0.0.1:18080/";
            var options = new NodeAgentOptions("membership", [Encoding.Contracts.Nodes.NodeRole.Server, Encoding.Contracts.Nodes.NodeRole.Worker], 1, data,
                localUrl, false, TimeSpan.FromSeconds(10), 8081, [], secretFile);
            var store = new WorkerIdentityStore(options);
            var enrollment = new WorkerEnrollmentService(options, store, new WorkerClusterDiscovery(options, NullLogger<WorkerClusterDiscovery>.Instance),
                null!, NullLogger<WorkerEnrollmentService>.Instance, Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()), launcher);

            // Arrivée dans un autre cluster : le serveur local s'arrête, le worker reste.
            await enrollment.RetireLocalServerAsync(clusterUrl, CancellationToken.None);
            Check(mode == new NodeMode(false, true), "A server+worker node that joins another cluster becomes a worker only.");
            await enrollment.RetireLocalServerAsync(clusterUrl, CancellationToken.None);
            Check(mode == new NodeMode(false, true), "Retiring the local server twice changes nothing.");

            // Rattaché au cluster distant : le départ le prévient avec son jeton, efface l'identité et rend le serveur local.
            var joined = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster B", clusterUrl, CancellationToken.None);
            await store.MarkEnrolledAsync(CancellationToken.None);
            leaveStatus = HttpStatusCode.Conflict;
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => enrollment.LeaveAsync(CancellationToken.None));
            var stillJoined = await store.GetAsync(CancellationToken.None);
            Check(stillJoined.IsEnrolled && stillJoined.Id == joined.Id && mode == new NodeMode(false, true), "A node busy with a task cannot leave: identity and mode are unchanged.");
            Check(busy.Message.Contains("traite encore"), "The refusal explains that the node is still processing.");

            leaveStatus = HttpStatusCode.NoContent;
            await enrollment.LeaveAsync(CancellationToken.None);
            var left = await store.GetAsync(CancellationToken.None);
            Check(leaves.Last().Path == $"/api/v1/nodes/{joined.Id:D}/leave" && leaves.Last().Token == joined.NodeToken, "The node tells its cluster, authenticated by its own token.");
            Check(!left.HasCredentials && left.Id != joined.Id, "After leaving, the node restarts from a fresh identity (a removed identity never comes back).");
            Check(mode == new NodeMode(true, true), "After leaving, the node gets its local server back (standalone node).");

            // Cluster distant injoignable : le départ reste possible, localement.
            var orphan = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster perdu", "http://127.0.0.1:1/", CancellationToken.None);
            await enrollment.LeaveAsync(CancellationToken.None);
            Check(!(await store.GetAsync(CancellationToken.None)).HasCredentials, "An unreachable cluster does not trap the node.");
            Check(orphan.NodeToken is not null, "Sanity: the abandoned enrollment had credentials.");

            // Cluster local du conteneur : rien à arrêter.
            mode = new NodeMode(true, true);
            await enrollment.RetireLocalServerAsync(localUrl, CancellationToken.None);
            Check(mode == new NodeMode(true, true), "Staying in the local cluster keeps the local server.");
            Console.WriteLine("PASS: cluster membership (leave, switch, local server retirement).");
        }
        finally
        {
            try { Directory.Delete(data, true); } catch (IOException) { }
        }
    }

    private static class Assert
    {
        public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T exception) { return exception; }
            throw new InvalidOperationException($"{typeof(T).Name} attendu.");
        }
    }
}
