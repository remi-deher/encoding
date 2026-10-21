using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;
using Encoding.Contracts.Executions;

namespace Encoding.Runtime.Tests;

internal static class FlowV3Scenarios
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static string TemplatePath() => Path.Combine(Harness.RepositoryRoot(), "..", "docs", "operations", "flow-templates", "hevc-aac-ass.flow.json");

    internal static string Json(FlowDefinition flow) =>
        JsonSerializer.Serialize(flow, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }).Replace("\r\n", "\n") + "\n";

    public static void Run(FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility)
    {
        var flow = FlowV3Template.Build(registry);
        var validation = validator.Validate(flow);
        Check(validation.IsValid, "The Crunchyroll flow is valid with the catalog components: " + string.Join("; ", validation.Issues.Select(item => item.Path + " " + item.Message)));

        // Le fichier importable est exactement ce graphe (ENCODING_UPDATE_TEMPLATE=1 le régénère après un changement voulu).
        var expected = Json(flow);
        if (Environment.GetEnvironmentVariable("ENCODING_UPDATE_TEMPLATE") == "1") { Directory.CreateDirectory(Path.GetDirectoryName(TemplatePath())!); File.WriteAllText(TemplatePath(), expected); }
        Check(File.Exists(TemplatePath()) && File.ReadAllText(TemplatePath()).Replace("\r\n", "\n") == expected, "docs/operations/flow-templates/hevc-aac-ass.flow.json matches the tested flow (regenerate with ENCODING_UPDATE_TEMPLATE=1).");
        var imported = validator.ValidateDocument(JsonDocument.Parse(File.ReadAllText(TemplatePath())).RootElement);
        Check(imported.Result.IsValid && imported.Definition is not null && imported.Definition.Id == FlowV3Template.FlowId, "The importable file passes the import validation.");

        // Tout chemin d'échec ou d'attente aboutit à une fin explicite : jamais d'impasse silencieuse.
        string? Next(string node, string output) => FlowPath.NextNodeId(flow, node, output);
        var components = flow.Nodes.ToDictionary(node => node.Id, node => registry.Resolve(node.Component)!);
        foreach (var node in flow.Nodes)
        {
            var descriptor = components[node.Id];
            if (descriptor.Kind == "terminal") continue;
            foreach (var output in descriptor.Outputs.Select(item => item.Id))
                Check(Next(node.Id, output) is not null, $"{node.Id}: output '{output}' is connected, so no step ends silently.");
        }
        Check(Next("playback", "playing") == "defer-playing" && Next("disk", "busy") == "defer-disk", "A playback or a busy disk defers the processing instead of waiting on a worker.");
        Check(flow.Nodes.Single(node => node.Id == "defer-disk").Configuration["delayMinutes"].GetInt32() == 2, "A busy disk retries after two minutes, like the original flow.");
        foreach (var failing in new[] { "disk", "snapshot", "prepare", "video", "audio", "execute", "subtitles", "fonts", "names" })
            Check(Next(failing, "error") == "fail", $"{failing}: an error ends the flow in failure (source untouched), including an ambiguous audio layout.");
        var audio = flow.Nodes.Single(node => node.Id == "audio").Configuration;
        Check(audio["unknownLayout"].GetString() == "infer", "Audio layouts are inferred, and refused (error output) when they stay ambiguous.");

        // Éligibilité : outils et versions de plugins exigés par le flow.
        var worker = Guid.NewGuid();
        NodeToolCapability[] Caps(string baseVersion, string plexVersion, bool mkv = true, bool ffmpeg = true) => [
            new("plugin/base", baseVersion, ["general"], []),
            new("plugin/video", "1.1.0", ["general"], []),
            new("plugin/audio", "1.0.0", ["general"], []),
            new("plugin/subtitles", "1.0.0", ["general"], []),
            new("plugin/probe", "1.0.0", ["general"], []),
            new("plugin/plex", plexVersion, ["general"], ["process"]),
            new("ffprobe", "7.0", ["video"], []),
            .. ffmpeg ? new[] { new NodeToolCapability("ffmpeg", "7.0", ["video"], ["cpu"], ["libx265", "aac", "ac3"]) } : [],
            .. mkv ? new[] { new NodeToolCapability("mkvtoolnix", "81.0", ["video"], []) } : []];
        var baseVersion = registry.Plugins.Single(item => item.Id == "base").Version; var plexVersion = registry.Plugins.Single(item => item.Id == "plex").Version;
        Check(eligibility.CanRun(flow, Caps(baseVersion, plexVersion), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])), "A worker with FFmpeg, MKVToolNix, Base and Plex at the right versions can run it.");
        Check(!eligibility.CanRun(flow, Caps(baseVersion, plexVersion, mkv: false), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])), "Without MKVToolNix nobody receives it.");
        Check(!eligibility.CanRun(flow, Caps(baseVersion, plexVersion, ffmpeg: false), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])), "Without FFmpeg nobody receives it.");
        Check(!eligibility.CanRun(flow, Caps("1.4.0", plexVersion), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])), "A worker that does not announce Base with the disk blocks never receives it.");
        Check(!eligibility.CanRun(flow, Caps(baseVersion, "1.0.0"), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])), "A worker with the old Plex plugin never receives it.");
    }
}
