using System.Text.Json;

namespace Encoding.NodeAgent.Runtime;

/// <summary>Launch metadata comes from the manifests shipped beside the isolated plugin process.</summary>
public static class BundledPluginCatalog
{
    public static IReadOnlyList<PluginLaunch> Scan(IConfiguration configuration)
    {
        if (!configuration.GetValue("Runtime:BundledPlugins:Enabled", true)) return [];
        var root = configuration["Runtime:BundledPlugins:Directory"] ?? "/opt/encoding/plugins";
        var result = new List<PluginLaunch>();
        foreach (var (id, folder) in new[] { ("plex", "plex"), ("sonarr", "arr"), ("radarr", "arr"), ("jellyfin", "jellyfin") })
        {
            if (!configuration.GetValue($"Runtime:BundledPlugins:{id}:Enabled", true)) continue;
            var directory = Path.GetFullPath(Path.Combine(root, folder));
            var script = Path.Combine(directory, "plugin.py");
            var manifest = Path.Combine(directory, "manifests", id + ".json");
            if (!File.Exists(script) || !File.Exists(manifest)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.GetProperty("id").GetString() != id)
                throw new IOException("Identité du manifeste de plugin livré incompatible.");
            result.Add(new PluginLaunch {
                Id = id, Version = document.RootElement.GetProperty("version").GetString()!,
                Executable = ProcessToolRunner.Find("python3") ?? "/usr/bin/python3",
                Arguments = folder == "arr" ? [script, id] : [script],
                ReadOnlyPaths = [directory]
            });
        }
        return result;
    }
}
