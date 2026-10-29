using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowPlacementPlannerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    private static (FlowComponentRegistry Registry, FlowPlacementPlanner Planner) CreatePlanner()
    {
        var registry = new FlowComponentRegistry(new ConfigurationBuilder().Build());
        var planner = new FlowPlacementPlanner(registry);
        return (registry, planner);
    }

    private static FlowNodeDefinition Node(FlowComponentRegistry registry, string id, string componentId, Dictionary<string, JsonElement>? config = null)
    {
        var reference = registry.FindReference(componentId) ?? throw new InvalidOperationException($"Composant inconnu : {componentId}");
        return new(id, null, reference, config ?? new Dictionary<string, JsonElement>());
    }

    private static FlowDefinition MakeFlow(string name, IReadOnlyList<FlowNodeDefinition> nodes, IReadOnlyList<FlowConnectionDefinition> connections) =>
        new(1, Guid.NewGuid(), 1, name, "video", nodes, connections);

    private static FlowDefinition Chain(params FlowNodeDefinition[] nodes)
    {
        var connections = new List<FlowConnectionDefinition>();
        for (var i = 0; i < nodes.Length - 1; i++)
        {
            connections.Add(new($"c{i}", nodes[i].Id, "success", nodes[i + 1].Id));
        }
        return MakeFlow("ChainFlow", nodes, connections);
    }

    private static NodeToolCapability Tool(string id, string version = "1.0.0", string[]? encoders = null, string[]? accelerations = null, NodeEncoderCapability[]? checks = null) =>
        new(id, version, ["video"], accelerations ?? ["cpu"], encoders ?? [], checks);

    [Fact]
    public void DirectUnified_WhenDirectNodeHasAllCapabilities()
    {
        var (registry, planner) = CreatePlanner();
        var nodeAId = Guid.NewGuid();

        var input = Node(registry, "input", "base-input-video");
        var cond = Node(registry, "cond", "conditions-video-codec", new() { ["codec"] = J("hevc") });
        var pub = Node(registry, "pub", "base-publish-result");
        var end = Node(registry, "end", "base-complete-flow");

        var flow = MakeFlow("Remux",
            [input, cond, pub, end],
            [
                new("c1", "input", "success", "cond"),
                new("c2", "cond", "yes", "pub"),
                new("c3", "pub", "success", "end")
            ]);

        var metadata = J(new
        {
            streams = new[]
            {
                new { codec_type = "video", codec_name = "hevc", width = 1920, height = 1080 }
            }
        });

        var nodeA = new NodePlacementCandidate(
            nodeAId,
            "Node-50-CPU",
            IsWorker: true,
            IsOnline: true,
            Capabilities: [
                Tool("ffprobe"),
                Tool("ffmpeg", "7.0.0", ["libx264"]),
                Tool("mkvtoolnix"),
                Tool("plugin/base", "1.9.0"),
                Tool("plugin/conditions", "1.1.0"),
                Tool("plugin/nodes", "1.8.0")
            ],
            Inventory: null,
            Identity: null,
            Access: new LibraryNodeAccessInfo(Guid.NewGuid(), "direct", true, true, true, "/media/lib", null));

        var plan = planner.Plan(flow, metadata, [nodeA], DateTimeOffset.UtcNow);

        Assert.Equal(FlowPlacementPlan.StrategyDirectUnified, plan.Strategy);
        Assert.Equal(nodeAId, plan.AssignedNodeId);
        Assert.Equal(nodeAId, plan.AccessNodeId);
        Assert.Null(plan.TranscodeNodeId);
        Assert.Empty(plan.MissingCapabilities);
        Assert.Contains("0 transfert réseau", plan.Reason);
    }

    [Fact]
    public void TranscodeRelay_WhenDirectNodeLacksHardwareEncoderButGpuNodeIsAvailable()
    {
        var (registry, planner) = CreatePlanner();
        var nodeAId = Guid.NewGuid();
        var nodeBId = Guid.NewGuid();
        var libId = Guid.NewGuid();

        var input = Node(registry, "input", "base-input-video");
        var cond = Node(registry, "cond", "conditions-video-codec", new() { ["codec"] = J("hevc") });
        var transcode = Node(registry, "transcode", "video-transcode", new() { ["videoCodec"] = J("hevc"), ["hardwareAcceleration"] = J("qsv") });
        var pub = Node(registry, "pub", "base-publish-result");
        var end = Node(registry, "end", "base-complete-flow");

        var flow = MakeFlow("Transcode",
            [input, cond, transcode, pub, end],
            [
                new("c1", "input", "success", "cond"),
                new("c2", "cond", "no", "transcode"),
                new("c3", "transcode", "success", "pub"),
                new("c4", "pub", "success", "end")
            ]);

        var metadata = J(new
        {
            streams = new[]
            {
                new { codec_type = "video", codec_name = "h264", width = 1920, height = 1080 }
            }
        });

        var nodeA = new NodePlacementCandidate(
            nodeAId,
            "Node-50-CPU",
            IsWorker: true,
            IsOnline: true,
            Capabilities: [
                Tool("ffprobe"),
                Tool("ffmpeg", "7.0.0", ["libx264"], ["cpu"]),
                Tool("mkvtoolnix"),
                Tool("plugin/base", "1.9.0"),
                Tool("plugin/conditions", "1.1.0"),
                Tool("plugin/video", "1.2.0"),
                Tool("plugin/nodes", "1.8.0")
            ],
            Inventory: null,
            Identity: null,
            Access: new LibraryNodeAccessInfo(libId, "direct", true, true, true, "/media/lib", null));

        var nodeB = new NodePlacementCandidate(
            nodeBId,
            "Node-51-GPU",
            IsWorker: true,
            IsOnline: true,
            Capabilities: [
                Tool("ffprobe"),
                Tool("ffmpeg", "7.0.0", ["hevc_qsv", "libx264"], ["qsv", "cpu"]),
                Tool("mkvtoolnix"),
                Tool("plugin/base", "1.9.0"),
                Tool("plugin/conditions", "1.1.0"),
                Tool("plugin/video", "1.2.0"),
                Tool("plugin/nodes", "1.8.0")
            ],
            Inventory: null,
            Identity: null,
            Access: new LibraryNodeAccessInfo(libId, "transfer", true, true, true, null, nodeAId));

        var plan = planner.Plan(flow, metadata, [nodeA, nodeB], DateTimeOffset.UtcNow);

        Assert.Equal(FlowPlacementPlan.StrategyTransferWorker, plan.Strategy);
        Assert.Equal(nodeBId, plan.AssignedNodeId);
        Assert.Equal(nodeAId, plan.AccessNodeId);
        Assert.Null(plan.TranscodeNodeId);
        Assert.Empty(plan.MissingCapabilities);
        Assert.Contains("transfert WebDAV", plan.Reason);
    }

    [Fact]
    public void SegmentedPlan_AssignsOnlyTheTranscodeStepToTheSpecialist()
    {
        var (registry, planner) = CreatePlanner();
        var library = Guid.NewGuid();
        var direct = Guid.NewGuid();
        var gpu = Guid.NewGuid();
        var flow = Chain(
            Node(registry, "input", "base-input-video"),
            Node(registry, "encode", "video-transcode", new() { ["videoCodec"] = J("hevc"), ["hardwareAcceleration"] = J("qsv") }),
            Node(registry, "remux", "mkv-remux-matroska"),
            Node(registry, "complete", "base-complete-flow"));
        var candidates = new[]
        {
            new NodePlacementCandidate(direct,"direct",true,true,
                [Tool("ffprobe"),Tool("ffmpeg",encoders:["libx264"],accelerations:["cpu"]),Tool("mkvtoolnix"),Tool("plugin/base","1.9.0"),Tool("plugin/video","1.2.0"),Tool("plugin/mkv","1.0.0")],
                null,null,new(library,"direct",true,true,true,"/media",null)),
            new NodePlacementCandidate(gpu,"gpu",true,true,
                [Tool("ffprobe"),Tool("ffmpeg",encoders:["hevc_qsv"],accelerations:["qsv"]),Tool("plugin/video","1.2.0")],
                null,null,new(library,"transfer",true,true,false,null,direct))
        };

        var plan = planner.Plan(flow, null, candidates, DateTimeOffset.UtcNow);

        Assert.True(plan.Strategy==FlowPlacementPlan.StrategySegmented,$"{plan.Strategy}: {plan.Reason}; missing={string.Join(',',plan.MissingCapabilities)}");
        Assert.Equal(direct, plan.NodeAssignments!["input"]);
        Assert.Equal(gpu, plan.NodeAssignments["encode"]);
        Assert.Equal(direct, plan.NodeAssignments["remux"]);
        Assert.Equal(direct, plan.NodeAssignments["complete"]);
    }

    [Fact]
    public void ExplicitNodeHandoff_IsARequiredBoundaryAndConstrainsTheNextSegment()
    {
        var (registry, planner) = CreatePlanner();
        var library=Guid.NewGuid(); var source=Guid.NewGuid(); var target=Guid.NewGuid();
        var flow=Chain(
            Node(registry,"input","base-input-video"),
            Node(registry,"handoff","nodes-handoff",new() { ["mode"]=J("node"),["targetNodeId"]=J(target.ToString("D")) }),
            Node(registry,"script","base-script",new() { ["language"]=J("python") }),
            Node(registry,"complete","base-complete-flow"));
        var candidates=new[]
        {
            new NodePlacementCandidate(source,"source",true,true,
                [Tool("ffprobe"),Tool("plugin/base","1.9.0"),Tool("plugin/nodes","1.8.0"),Tool("python")],
                null,null,new(library,"direct",true,true,true,"/media",null)),
            new NodePlacementCandidate(target,"target",true,true,
                [Tool("ffprobe"),Tool("plugin/base","1.9.0"),Tool("python")],
                null,null,new(library,"transfer",true,true,false,null,source))
        };

        var plan=planner.Plan(flow,null,candidates,DateTimeOffset.UtcNow);

        Assert.Equal(FlowPlacementPlan.StrategySegmented,plan.Strategy);
        Assert.Equal(source,plan.NodeAssignments!["handoff"]);
        Assert.Equal(target,plan.NodeAssignments["script"]);
    }

    [Fact]
    public void Impossible_WhenNoNodeHasRequiredCapability()
    {
        var (registry, planner) = CreatePlanner();
        var nodeAId = Guid.NewGuid();

        var input = Node(registry, "input", "base-input-video");
        var script = Node(registry, "script", "base-script", new() { ["language"] = J("python") });
        var flow = Chain(input, script);

        var nodeA = new NodePlacementCandidate(
            nodeAId,
            "Node-50",
            IsWorker: true,
            IsOnline: true,
            Capabilities: [
                Tool("ffprobe"),
                Tool("ffmpeg"),
                Tool("plugin/base", "1.9.0")
            ],
            Inventory: null,
            Identity: null,
            Access: new LibraryNodeAccessInfo(Guid.NewGuid(), "direct", true, true, true, "/media", null));

        var plan = planner.Plan(flow, null, [nodeA], DateTimeOffset.UtcNow);

        Assert.Equal(FlowPlacementPlan.StrategyImpossible, plan.Strategy);
        Assert.Null(plan.AssignedNodeId);
        Assert.Contains("python", string.Join(" ", plan.MissingCapabilities));
        Assert.Contains("Placement impossible", plan.Reason);
    }

    [Fact]
    public void ConditionBranchPruning_AvoidsDemandingGpu_WhenSourceIsAlreadyTargetCodec()
    {
        var (registry, planner) = CreatePlanner();
        var nodeAId = Guid.NewGuid();

        var input = Node(registry, "input", "base-input-video");
        var cond = Node(registry, "cond", "conditions-video-codec", new() { ["codec"] = J("hevc") });
        var copy = Node(registry, "copy", "base-variable", new() { ["name"] = J("action"), ["value"] = J("copy") });
        var transcode = Node(registry, "transcode", "video-transcode", new() { ["videoCodec"] = J("hevc"), ["hardwareAcceleration"] = J("qsv") });
        var end = Node(registry, "end", "base-complete-flow");

        var flow = MakeFlow("SmartCodec",
            [input, cond, copy, transcode, end],
            [
                new("c1", "input", "success", "cond"),
                new("c2", "cond", "yes", "copy"),
                new("c3", "cond", "no", "transcode"),
                new("c4", "copy", "success", "end"),
                new("c5", "transcode", "success", "end")
            ]);

        var metadataHevc = J(new
        {
            streams = new[]
            {
                new { codec_type = "video", codec_name = "hevc", width = 1920, height = 1080 }
            }
        });

        var nodeACpuOnly = new NodePlacementCandidate(
            nodeAId,
            "Node-50-CPU",
            IsWorker: true,
            IsOnline: true,
            Capabilities: [
                Tool("ffprobe"),
                Tool("ffmpeg", "7.0.0", ["libx264"], ["cpu"]),
                Tool("plugin/base", "1.9.0"),
                Tool("plugin/conditions", "1.1.0"),
                Tool("plugin/video", "1.2.0")
            ],
            Inventory: null,
            Identity: null,
            Access: new LibraryNodeAccessInfo(Guid.NewGuid(), "direct", true, true, true, "/media", null));

        var plan = planner.Plan(flow, metadataHevc, [nodeACpuOnly], DateTimeOffset.UtcNow);

        Assert.Equal(FlowPlacementPlan.StrategyDirectUnified, plan.Strategy);
        Assert.Equal(nodeAId, plan.AssignedNodeId);
        Assert.Empty(plan.MissingCapabilities);
    }

    [Fact]
    public void NodeRunConstraint_ForcesSpecificNode()
    {
        var (registry, planner) = CreatePlanner();
        var nodeAId = Guid.NewGuid();
        var nodeBId = Guid.NewGuid();

        var input = Node(registry, "input", "base-input-video");
        var constraint = Node(registry, "constraint", "nodes-run-on-node", new() { ["targetNodeId"] = J(nodeBId.ToString("D")) });
        var end = Node(registry, "end", "base-complete-flow");
        var flow = Chain(input, constraint, end);

        var nodeA = new NodePlacementCandidate(
            nodeAId, "Node-A", true, true, [Tool("ffprobe"), Tool("plugin/base", "1.9.0"), Tool("plugin/nodes", "1.8.0")],
            null, null, new LibraryNodeAccessInfo(Guid.NewGuid(), "direct", true, true, true, "/media", null));

        var nodeB = new NodePlacementCandidate(
            nodeBId, "Node-B", true, true, [Tool("ffprobe"), Tool("plugin/base", "1.9.0"), Tool("plugin/nodes", "1.8.0")],
            null, null, new LibraryNodeAccessInfo(Guid.NewGuid(), "direct", true, true, true, "/media", null));

        var plan = planner.Plan(flow, null, [nodeA, nodeB], DateTimeOffset.UtcNow);

        Assert.Equal(nodeBId, plan.AssignedNodeId);
    }

    [Fact]
    public void FontSetsConstraint_RequiresNodeInventoryToContainFont()
    {
        var (registry, planner) = CreatePlanner();
        var nodeAId = Guid.NewGuid();
        var nodeBId = Guid.NewGuid();

        var input = Node(registry, "input", "base-input-video");
        var fonts = Node(registry, "fonts", "base-add-fonts", new() { ["fontSet"] = J("Crunchyroll") });
        var end = Node(registry, "end", "base-complete-flow");
        var flow = Chain(input, fonts, end);

        // Nœud A n'a pas les polices
        var nodeA = new NodePlacementCandidate(
            nodeAId, "Node-A", true, true, [Tool("ffprobe"), Tool("mkvtoolnix"), Tool("plugin/base", "1.9.0")],
            new NodeInventory("linux", "x64", 4, 8000, FontSets: ["Standard"]),
            null, new LibraryNodeAccessInfo(Guid.NewGuid(), "direct", true, true, true, "/media", null));

        // Nœud B a les polices Crunchyroll
        var nodeB = new NodePlacementCandidate(
            nodeBId, "Node-B", true, true, [Tool("ffprobe"), Tool("mkvtoolnix"), Tool("plugin/base", "1.9.0")],
            new NodeInventory("linux", "x64", 4, 8000, FontSets: ["Crunchyroll", "Standard"]),
            null, new LibraryNodeAccessInfo(Guid.NewGuid(), "direct", true, true, true, "/media", null));

        var plan = planner.Plan(flow, null, [nodeA, nodeB], DateTimeOffset.UtcNow);

        Assert.Equal(nodeBId, plan.AssignedNodeId);
    }
}
