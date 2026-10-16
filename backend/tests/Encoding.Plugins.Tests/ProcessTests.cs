using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Plugins;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Plugin Sonarr de bout en bout par le chemin du worker : PluginProcessHost, poignée de main, bac à sable Landlock.</summary>
internal static class ProcessTests
{
    public static async Task RunAsync()
    {
        var python = ProcessToolRunner.Find("python3");
        if (python is null)
        {
            Console.WriteLine("SKIP: plugin host test requires python3.");
            return;
        }
        var plugins = Path.Combine(RepositoryRoot(), "backend", "plugins");
        var library = Guid.NewGuid();
        var commands = new List<(string Body, string Key)>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var sonarr = builder.Build();
        sonarr.MapGet("/api/v5/system/status", () => Results.NotFound());
        sonarr.MapGet("/api/v3/system/status", () => Results.Ok(new { }));
        sonarr.MapGet("/api/v3/series", () => Results.Ok(new[] { new { id = 7, path = "/tv/Show" } }));
        sonarr.MapPost("/api/v3/command", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            commands.Add((await reader.ReadToEndAsync(), request.Headers["X-Api-Key"].ToString()));
            return Results.Created("/api/v3/command/1", new { });
        });
        await sonarr.StartAsync();

        var data = Path.Combine(Path.GetTempPath(), "encoding-plugin-host-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(data, "work");
        Directory.CreateDirectory(workspace);
        try
        {
            // Même déclaration que sur un worker : interpréteur, script, dossiers lisibles (script et SDK).
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:Plugins:0:Id"] = "sonarr", ["Runtime:Plugins:0:Version"] = "1.0.0", ["Runtime:Plugins:0:Executable"] = python,
                ["Runtime:Plugins:0:Arguments:0"] = "-B", ["Runtime:Plugins:0:Arguments:1"] = Path.Combine(plugins, "arr", "plugin.py"), ["Runtime:Plugins:0:Arguments:2"] = "sonarr",
                ["Runtime:Plugins:0:ReadOnlyPaths:0"] = plugins, ["Runtime:Plugins:0:ReadOnlyPaths:1"] = Path.Combine(RepositoryRoot(), "backend", "src", "Encoding.Api", "Flows", "Catalog"),
                ["Runtime:Plugins:1:Id"] = "plex", ["Runtime:Plugins:1:Version"] = "1.1.0", ["Runtime:Plugins:1:Executable"] = python,
                ["Runtime:Plugins:1:Arguments:0"] = "-B", ["Runtime:Plugins:1:Arguments:1"] = Path.Combine(plugins, "plex", "plugin.py"),
                ["Runtime:Plugins:1:ReadOnlyPaths:0"] = plugins, ["Runtime:Plugins:1:ReadOnlyPaths:1"] = Path.Combine(RepositoryRoot(), "backend", "src", "Encoding.Api", "Flows", "Catalog"),
                ["Runtime:Plugins:2:Id"] = "jellyfin", ["Runtime:Plugins:2:Version"] = "1.0.0", ["Runtime:Plugins:2:Executable"] = python,
                ["Runtime:Plugins:2:Arguments:0"] = "-B", ["Runtime:Plugins:2:Arguments:1"] = Path.Combine(plugins, "jellyfin", "plugin.py"),
                ["Runtime:Plugins:2:ReadOnlyPaths:0"] = plugins, ["Runtime:Plugins:2:ReadOnlyPaths:1"] = Path.Combine(RepositoryRoot(), "backend", "src", "Encoding.Api", "Flows", "Catalog")
            }).Build();
            var options = new NodeAgentOptions("plugin-test", [NodeRole.Worker], 1, Path.Combine(data, "node"), null, false, TimeSpan.FromSeconds(10), 8081);
            var host = new PluginProcessHost(new ProcessToolRunner(), configuration, options);
            var registry = new Encoding.Api.Flows.FlowComponentRegistry(new ConfigurationBuilder().Build());
            var manifest = registry.Plugins.Single(plugin => plugin.Id == "sonarr");
            var component = manifest.Components.Single(item => item.Id == "sonarr-refresh-series");
            var invocation = new FlowStepInvocation(1, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), 4, "refresh",
                new(manifest.Id, manifest.Version, component.Id, component.Version, component.ConfigurationSchema.Version), new Dictionary<string, JsonElement>(),
                new(null, "current.mkv", null, null, new Dictionary<string, JsonElement>(), "Show/S01/e1.mkv", library), DateTimeOffset.UtcNow.AddMinutes(2),
                new Dictionary<string, JsonElement> { ["baseUrl"] = J(sonarr.Urls.Single()), ["apiKey"] = J("host-key"), ["pathMappings"] = J($"{library}=/tv") });
            try
            {
                var described = await host.DescribeAsync(host.Installed.Single(item => item.Id == "sonarr"), CancellationToken.None);
                Check(described.Id == "sonarr" && described.Version == manifest.Version && described.Components.Count == manifest.Components.Count, "The worker handshake accepts the plugin and its manifest matches the server catalog.");
                foreach (var id in new[] { "plex", "jellyfin" })
                {
                    var expected = registry.Plugins.Single(item => item.Id == id);
                    var actual = await host.DescribeAsync(host.Installed.Single(item => item.Id == id), CancellationToken.None);
                    Check(actual.Id == id && actual.Version == expected.Version && actual.Components.Select(c => c.Id).SequenceEqual(expected.Components.Select(c => c.Id)), $"The worker handshake accepts the {id} plugin and matches the server catalog.");
                }
                var response = await host.ExecuteAsync(invocation, workspace, CancellationToken.None);
                Check(response.OutputId == "success" && response.InvocationId == invocation.InvocationId && response.LeaseEpoch == 4, "The worker host validates the plugin answer.");
                Check(commands.Count == 1 && commands[0].Key == "host-key" && commands[0].Body.Contains("RescanSeries") && commands[0].Body.Contains("7"), "The sandboxed plugin rescanned the series that contains the file.");
                Console.WriteLine("PASS: PluginProcessHost handshake and execution of the Sonarr plugin (sandbox included).");
            }
            catch (IOException exception) when (exception.Message.Contains("Landlock", StringComparison.Ordinal))
            {
                Console.WriteLine("SKIP: PluginProcessHost requires Linux Landlock on this machine.");
            }
        }
        finally { Directory.Delete(data, true); }
    }
}
