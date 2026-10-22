using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Flows;

namespace Encoding.Runtime.Tests;

internal static class FlowV3ClusterScenarios
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static string TemplatePath() => Path.Combine(Harness.RepositoryRoot(), "..", "docs", "operations", "flow-templates", "hevc-aac-ass-cluster.flow.json");

    public static void Run(FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility)
    {
        var flow = FlowV3ClusterTemplate.Build(registry);
        var validation = validator.Validate(flow);
        Check(validation.IsValid, "The cluster flow is valid: " + string.Join("; ", validation.Issues.Select(item => item.Path + " " + item.Message)));
        var expected = FlowV3Scenarios.Json(flow);
        if (Environment.GetEnvironmentVariable("ENCODING_UPDATE_TEMPLATE") == "1") File.WriteAllText(TemplatePath(), expected);
        Check(File.Exists(TemplatePath()) && File.ReadAllText(TemplatePath()).Replace("\r\n", "\n") == expected, "docs/operations/flow-templates/hevc-aac-ass-cluster.flow.json matches the tested flow (regenerate with ENCODING_UPDATE_TEMPLATE=1).");
        var imported = validator.ValidateDocument(JsonDocument.Parse(File.ReadAllText(TemplatePath())).RootElement);
        Check(imported.Result.IsValid && imported.Definition?.Id == FlowV3ClusterTemplate.FlowId, "The importable file passes the import validation.");
        Check(flow.Nodes.All(node => node.Component.ComponentId != "base-disk-lock"), "The cluster flow takes no disk lock.");
        Check(eligibility.RequiresDirectAccess(flow), "The first segment is only taken by a node that mounts the library, so a transfer node cannot take it and then have nowhere to relay.");
        Check(!eligibility.RequiresDirectAccess(flow, "need"), "The relay destination receives the file by transfer.");

        string? Next(string node, string output) => FlowPath.NextNodeId(flow, node, output);
        foreach (var node in flow.Nodes)
        {
            var descriptor = registry.Resolve(node.Component)!;
            if (descriptor.Kind == "terminal") continue;
            foreach (var output in descriptor.Outputs.Select(item => item.Id))
                Check(Next(node.Id, output) is not null, $"{node.Id}: output '{output}' is connected.");
        }

        var baseVersion = registry.Plugins.Single(item => item.Id == "base").Version; var plexVersion = registry.Plugins.Single(item => item.Id == "plex").Version;
        var nodesVersion = registry.Plugins.Single(item => item.Id == "nodes").Version;
        NodeToolCapability[] Worker(string[] usable, string[] accelerations) => [
            new("plugin/base", baseVersion, ["general"], []), new("plugin/video", "1.1.0", ["general"], []), new("plugin/audio", "1.0.0", ["general"], []),
            new("plugin/subtitles", "1.0.0", ["general"], []), new("plugin/probe", "1.0.0", ["general"], []), new("plugin/plex", plexVersion, ["general"], ["process"]),
            new("plugin/nodes", nodesVersion, ["general"], []), new("ffprobe", "7.0", ["video"], []), new("mkvtoolnix", "81.0", ["video"], []),
            new("ffmpeg", "7.0", ["video"], accelerations, [.. usable, "aac", "ac3"], [.. usable.Select(id => new NodeEncoderCapability(id, "usable"))])];
        var inventory = new NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"]);
        var worker = Guid.NewGuid();
        var cpuNode = Worker(["libx265"], ["cpu"]); var gpuNode = Worker(["libx265", "hevc_qsv"], ["cpu", "qsv"]);
        Check(eligibility.CanRun(flow, gpuNode, worker, inventory: inventory), "A node with a usable QSV encoder takes the whole flow.");
        Check(eligibility.CanRun(flow, cpuNode, worker, inventory: inventory), "A CPU-only node that mounts files takes the flow too: its encoding branch is not asked of it.");
        // Après le relais : seul un nœud avec un encodeur utilisable du profil reçoit la suite.
        Check(eligibility.CanRun(flow, gpuNode, worker, "need", inventory: inventory), "The relay destination needs a usable encoder of the profile: a QSV node qualifies.");
        Check(!eligibility.CanRun(flow, cpuNode, worker, "need", inventory: inventory), "The relay destination needs a usable encoder of the profile: a CPU-only node does not.");
        Check(!eligibility.CanRun(flow, Worker(["libx265", "hevc_qsv"], ["cpu", "qsv"]).Where(cap => cap.ToolId != "mkvtoolnix").ToArray(), worker, "need", inventory: inventory), "Without MKVToolNix the destination is refused.");
    }
}
