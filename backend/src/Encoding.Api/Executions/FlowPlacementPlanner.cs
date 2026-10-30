using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Executions;

public sealed record NodePlacementCandidate(
    Guid NodeId,
    string? Name,
    bool IsWorker,
    bool IsOnline,
    IReadOnlyList<NodeToolCapability> Capabilities,
    NodeInventory? Inventory,
    NodeIdentityResponse? Identity,
    LibraryNodeAccessInfo? Access,
    bool HasAvailableCapacity = true);

public sealed record LibraryNodeAccessInfo(
    Guid LibraryId,
    string Mode,
    bool IsEnabled,
    bool CanRead,
    bool CanWrite,
    string? LocalPath,
    Guid? TransferSourceNodeId);

public sealed record VideoTranscodeRequirement(
    string Codec,
    string Mode,
    string? EncoderId,
    EncodingProfileSnapshot? Profile,
    string? HardwareAcceleration,
    bool RetryWithCpu);

public sealed record NodeRunConstraint(
    Guid? TargetNodeId,
    Guid? TargetGroupId,
    string? RequiredLabels);

public sealed record CapabilityConstraint(
    string ToolId,
    string? MinimumVersion,
    string? RequiredCapability,
    string? RequiredEncoder,
    string? EncoderKind);

public sealed record FlowPathRequirements(
    IReadOnlyList<string> NodeIds,
    bool RequiresDirectAccess,
    bool PrefersDirectAccess,
    bool RequiresWriteAccess,
    IReadOnlySet<string> RequiredTools,
    IReadOnlySet<string> RequiredPlugins,
    IReadOnlyList<VideoTranscodeRequirement> VideoTranscodes,
    IReadOnlySet<string> AudioEncoders,
    IReadOnlySet<string> FontSets,
    IReadOnlyList<NodeRunConstraint> RunConstraints,
    IReadOnlyList<CapabilityConstraint> CapabilityConstraints);

/// <summary>
/// Planificateur de placement de flow de bout en bout (ADR 0024).
/// Analyse les exigences complètes de chaque chemin et sélectionne dès l'entrée le nœud optimal
/// pour minimiser les transferts réseau (priorité accès direct, plan en 3 phases si transcode distant).
/// </summary>
public sealed class FlowPlacementPlanner(FlowComponentRegistry registry)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FlowPlacementPlan Plan(
        FlowDefinition flow,
        JsonElement? sourceMetadata,
        IReadOnlyList<NodePlacementCandidate> candidates,
        DateTimeOffset now)
    {
        var paths = ExtractPathRequirements(flow, sourceMetadata, candidates);
        if (paths.Count == 0)
        {
            return new(
                FlowPlacementPlan.StrategyImpossible,
                null, null, null,
                "Placement impossible : aucun chemin d’exécution valide dans le flow.",
                [], ["aucun_chemin_valide"], now);
        }

        var allRequiredCaps = CollectCapabilitiesNames(paths);
        var hasFixedTarget = flow.Nodes.Any(node => node.Component.PluginId == "nodes"
            && (node.Component.ComponentId == "nodes-run-on-node"
                || node.Component.ComponentId == "nodes-handoff" && node.Configuration.TryGetValue("mode",out var mode)
                    && mode.ValueKind==JsonValueKind.String && mode.GetString() is "node" or "selector"));
        var capacityCandidates = candidates.Any(candidate=>candidate.HasAvailableCapacity)
            ? candidates.Where(candidate=>candidate.HasAvailableCapacity).ToArray() : candidates;
        var placementCandidates = hasFixedTarget ? candidates : capacityCandidates;
        // Un relais explicite est une frontière d'exécution du flow. Les stratégies unifiées
        // ne doivent pas masquer cette frontière ni annoncer un nombre de transferts inférieur.
        var hasExplicitHandoff = paths.SelectMany(path => path.NodeIds)
            .Select(id => flow.Nodes.First(node => node.Id == id)).Any(IsWorkerHandoff);

        // 1. Nœuds d'accès direct en ligne
        var directAccessNodes = candidates
            .Where(c => c.IsOnline && c.IsWorker && c.Access is { IsEnabled: true, Mode: "direct", CanRead: true, LocalPath: not null })
            .ToList();

        // Cas 1 : Priorité 1 — Un nœud d'accès direct peut exécuter tous les chemins applicables
        foreach (var directNode in placementCandidates.Where(c=>directAccessNodes.Any(direct=>direct.NodeId==c.NodeId) && !hasExplicitHandoff))
        {
            if (paths.All(p => NodeSatisfiesPath(directNode, p, out _)))
            {
                var nodeLabel = directNode.Name ?? directNode.NodeId.ToString("D");
                return new(
                    FlowPlacementPlan.StrategyDirectUnified,
                    directNode.NodeId,
                    directNode.NodeId,
                    null,
                    $"Placement direct unifié : le nœud d’accès local {nodeLabel} prend l’intégralité du flow (0 transfert réseau).",
                    allRequiredCaps,
                    [],
                    now);
            }
        }

        // Priorité 2 : essayer d'abord un worker capable de tout le flow, même s'il faut lui transférer la source.
        var transferWorkers = placementCandidates
            .Where(c => c.IsOnline && c.IsWorker && c.Access is { IsEnabled: true, CanRead: true })
            .ToList();

        var canPublishAtSource = directAccessNodes.Any(candidate => candidate.Access is { CanWrite: true });
        foreach (var worker in transferWorkers.Where(_ => !hasExplicitHandoff))
        {
            if ((!paths.Any(path => path.RequiresWriteAccess) || canPublishAtSource)
                && paths.All(path => NodeSatisfiesPath(worker, path with { RequiresWriteAccess = false }, out _)))
            {
                var workerLabel = worker.Name ?? worker.NodeId.ToString("D");
                return new(
                    FlowPlacementPlan.StrategyTransferWorker,
                    worker.NodeId,
                    directAccessNodes.FirstOrDefault()?.NodeId,
                    null,
                    $"Placement avec transfert : le worker {workerLabel} prend la tâche via transfert WebDAV.",
                    allRequiredCaps,
                    [],
                    now,
                    flow.Nodes.ToDictionary(node => node.Id, _ => worker.NodeId, StringComparer.Ordinal));
            }
        }

        // Priorité 3 : minimiser exactement le nombre de transitions sur les arcs du graphe retenu.
        // Les branches non résolues sont planifiées de façon conservatrice :
        // tous leurs arcs sont pris en compte. Les conditions natives déjà évaluables sur les métadonnées
        // sont élaguées par ExtractPathRequirements avant cette étape.
        var segmented = PlanSegments(flow, paths, placementCandidates, now,sourceMetadata);
        if (segmented is not null) return segmented;

        // Cas 4 : Aucun nœud ne convient
        var missingReport = new List<string>();
        foreach (var p in paths)
        {
            var pathMissing = new List<string>();
            foreach (var c in candidates.Where(cand => cand.IsOnline && cand.IsWorker))
            {
                NodeSatisfiesPath(c, p, out var m);
                pathMissing.AddRange(m);
            }
            missingReport.AddRange(pathMissing);
        }
        var distinctMissing = missingReport.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctMissing.Count == 0) distinctMissing.Add("aucun_worker_disponible");

        return new(
            FlowPlacementPlan.StrategyImpossible,
            null,
            null,
            null,
            $"Placement impossible : aucun worker ne satisfait les capacités requises ({string.Join(", ", distinctMissing)}).",
            allRequiredCaps,
            distinctMissing,
            now);
    }

    private FlowPlacementPlan? PlanSegments(FlowDefinition flow, IReadOnlyList<FlowPathRequirements> paths,
        IReadOnlyList<NodePlacementCandidate> candidates, DateTimeOffset now,JsonElement? sourceMetadata)
    {
        var reachable = paths.SelectMany(path => path.NodeIds).ToHashSet(StringComparer.Ordinal);
        var nodes = flow.Nodes.Where(node => reachable.Contains(node.Id)).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var pathEdges=paths.SelectMany(path=>path.NodeIds.Zip(path.NodeIds.Skip(1),(source,target)=>(source,target))).ToHashSet();
        var incoming = flow.Connections.Where(edge => pathEdges.Contains((edge.SourceNodeId,edge.TargetNodeId)))
            .GroupBy(edge => edge.TargetNodeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var roots = nodes.Keys.Where(id => !incoming.ContainsKey(id)).ToArray();
        if (roots.Length != 1) return null;

        var ordered = new List<string>();
        var degrees=incoming.ToDictionary(pair=>pair.Key,pair=>pair.Value.Length,StringComparer.Ordinal);
        var ready=new Queue<string>(roots.Order(StringComparer.Ordinal));
        while(ready.TryDequeue(out var current))
        {
            ordered.Add(current);
            foreach(var edge in flow.Connections.Where(edge=>edge.SourceNodeId==current && pathEdges.Contains((edge.SourceNodeId,edge.TargetNodeId))))
            {
                degrees[edge.TargetNodeId]--;
                if(degrees[edge.TargetNodeId]==0) ready.Enqueue(edge.TargetNodeId);
            }
        }
        if (ordered.Count != nodes.Count) return null;

        var requirements = nodes.ToDictionary(pair => pair.Key, pair => BuildRequirements([pair.Value],sourceMetadata), StringComparer.Ordinal);
        var directRequired=new HashSet<string>(StringComparer.Ordinal);
        foreach(var path in paths)
        {
            var direct=false;
            foreach(var id in path.NodeIds)
            {
                var node=nodes[id];
                if(node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-require-direct-access"
                    && !(node.Configuration.TryGetValue("mode",out var directMode) && directMode.ValueKind==JsonValueKind.String && directMode.GetString()=="prefer")) direct=true;
                if(direct) directRequired.Add(id);
                if(RuntimeHandoffPolicy.IsHandoff(node)) direct=false;
            }
        }
        foreach(var id in directRequired) requirements[id]=requirements[id] with { RequiresDirectAccess=true };
        var available = requirements.ToDictionary(pair => pair.Key,
            pair => candidates.Where(candidate => candidate.IsOnline && candidate.IsWorker
                && (candidate.Access?.Mode=="direct" ? NodeSatisfiesPath(candidate,pair.Value,out _)
                    : NodeSatisfiesPath(candidate,pair.Value with { RequiresWriteAccess=false },out _))
                && FitsExplicitHandoff(flow,incoming.GetValueOrDefault(pair.Key)??[],candidate))
                .Select(candidate => candidate.NodeId).ToArray(), StringComparer.Ordinal);
        var missingNodes = available.Where(pair => pair.Value.Length == 0).Select(pair => pair.Key).ToArray();
        if (missingNodes.Length > 0) return null;

        const int searchBudget=500_000;
        var explored=0; var bestCost=int.MaxValue;
        Dictionary<string,Guid>? best=null;
        var assignments=new Dictionary<string,Guid>(StringComparer.Ordinal);
        void Search(int index,int cost)
        {
            if(cost>=bestCost || ++explored>searchBudget) return;
            if(index==ordered.Count) { bestCost=cost; best=new(assignments,StringComparer.Ordinal); return; }
            var id=ordered[index];
            var parents=incoming.GetValueOrDefault(id)??[];
            foreach(var worker in available[id]
                .OrderBy(candidate=>candidates.First(item=>item.NodeId==candidate).Access?.Mode=="direct"?0:1)
                .ThenBy(candidate=>candidate))
            {
                if(parents.Any(edge => flow.Nodes.First(node => node.Id == edge.SourceNodeId) is { } source
                    && IsWorkerHandoff(source)
                    && assignments.TryGetValue(edge.SourceNodeId,out var priorWorker) && priorWorker==worker)) continue;
                var added=parents.Count(edge=>assignments.TryGetValue(edge.SourceNodeId,out var prior)&&prior!=worker);
                if(cost+added>=bestCost) continue;
                assignments[id]=worker; Search(index+1,cost+added); assignments.Remove(id);
                if(explored>searchBudget) return;
            }
        }
        Search(0,0);
        if(explored>searchBudget)
            return new(FlowPlacementPlan.StrategyImpossible,null,null,null,
                "Placement impossible à optimiser dans la limite de recherche du graphe ; simplifiez les branches ou réduisez le nombre de workers candidats.",
                CollectCapabilitiesNames(paths),["placement_search_limit"],now);
        if(best is null) return null;
        assignments=best;

        var distinctWorkers = assignments.Values.Distinct().Count();
        if (distinctWorkers == 1) return null; // Le worker entier aurait dû être sélectionné ci-dessus.
        var accessNode = candidates.FirstOrDefault(candidate => candidate.IsOnline && candidate.IsWorker
            && candidate.Access is { IsEnabled: true, Mode: "direct", CanRead: true, CanWrite: true, LocalPath: not null });
        return new(FlowPlacementPlan.StrategySegmented, assignments.GetValueOrDefault(roots[0]), accessNode?.NodeId,
            null,
            "Placement segmenté : les blocs sont attribués à des workers compatibles ; les transitions suivent les checkpoints durables. Les branches non déterminées sont planifiées sur l’union de leurs chemins.",
            CollectCapabilitiesNames(paths), [], now, assignments);
    }

    private static bool FitsExplicitHandoff(FlowDefinition flow,IReadOnlyList<FlowConnectionDefinition> incoming,NodePlacementCandidate candidate)
    {
        foreach(var edge in incoming)
        {
            var source=flow.Nodes.FirstOrDefault(node=>node.Id==edge.SourceNodeId);
            if(source is null || !RuntimeHandoffPolicy.IsHandoff(source)) continue;
            var mode=source.Configuration.TryGetValue("mode",out var configured)?configured.GetString():"eligible";
            if(mode=="node" && source.Configuration.TryGetValue("targetNodeId",out var target)
                && Guid.TryParse(target.GetString(),out var id) && id!=candidate.NodeId) return false;
            if(mode=="selector")
            {
                Guid? group=null;
                if(source.Configuration.TryGetValue("targetGroupId",out var groupValue) && Guid.TryParse(groupValue.GetString(),out var groupId)) group=groupId;
                var labels=source.Configuration.TryGetValue("requiredLabels",out var labelValue)?labelValue.GetString():null;
                if(group is not null && candidate.Identity?.GroupIds.Contains(group.Value)!=true
                    || !NodeLabelSelector.Matches(labels,candidate.Identity?.EffectiveLabels)) return false;
            }
        }
        return true;
    }

    private static bool IsWorkerHandoff(FlowNodeDefinition node) => RuntimeHandoffPolicy.IsHandoff(node)
        && !(node.Configuration.TryGetValue("mode",out var mode) && mode.ValueKind==JsonValueKind.String && mode.GetString()=="server");

    public IReadOnlyList<FlowPathRequirements> ExtractPathRequirements(FlowDefinition flow, JsonElement? sourceMetadata, IReadOnlyList<NodePlacementCandidate>? candidates = null)
    {
        var inputNode = flow.Nodes.FirstOrDefault(n => registry.Resolve(n.Component)?.Kind == "input");
        if (inputNode is null) return [];

        var outgoing = flow.Connections.ToLookup(c => c.SourceNodeId);
        var nodesById = flow.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var paths = new List<List<FlowNodeDefinition>>();
        var currentPath = new List<FlowNodeDefinition>();
        var visitedInCurrentPath = new HashSet<string>(StringComparer.Ordinal);

        void Explore(string nodeId)
        {
            if (!nodesById.TryGetValue(nodeId, out var node)) return;
            if (!visitedInCurrentPath.Add(nodeId)) return;
            currentPath.Add(node);

            var edges = outgoing[nodeId].ToList();
            if (edges.Count == 0)
            {
                paths.Add(new(currentPath));
            }
            else
            {
                // Élagage des conditions si les métadonnées de la source sont disponibles
                string? filterOutput = null;
                if (sourceMetadata is { } meta && node.Component.PluginId == "conditions" && MediaConditionEvaluator.IsMediaCondition(node.Component.ComponentId))
                {
                    try
                    {
                        filterOutput = MediaConditionEvaluator.Evaluate(node.Component.ComponentId, node.Configuration, meta);
                    }
                    catch
                    {
                        filterOutput = null;
                    }
                }
                else if (sourceMetadata is { } matchMeta && node.Component.PluginId == "conditions" && node.Component.ComponentId == "conditions-match"
                    && node.Configuration.TryGetValue("rules", out var rulesJson))
                {
                    try
                    {
                        var tree = ConditionTree.Parse(rulesJson, flow.MediaType);
                        if (IsPureMediaCondition(tree))
                        {
                            var ctx = new ConditionEvaluationContext(
                                new("", ".mkv", new Dictionary<string, JsonElement>(StringComparer.Ordinal), matchMeta, matchMeta),
                                Guid.Empty, Guid.Empty, null, "", "");
                            filterOutput = ConditionTree.Evaluate(tree, ctx) ? "yes" : "no";
                        }
                    }
                    catch
                    {
                        filterOutput = null;
                    }
                }
                else if (candidates is not null && node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-library-access" && RuntimeHandoffPolicy.LibraryAccessNeedsEncoder(node))
                {
                    // Si aucun nœud à accès direct ne possède d'encodeur matériel compatible avec le profil du flow,
                    // la branche « oui » ne sera jamais empruntée par le nœud source direct : ne pas l'imposer.
                    var anyDirectCanEncode = candidates.Any(c =>
                        c.IsOnline && c.IsWorker
                        && c.Access is { IsEnabled: true, Mode: "direct", CanRead: true, LocalPath: not null }
                        && MediaCodecPolicy.ProfileEncoderUsable(flow, c.Capabilities.FirstOrDefault(cap => cap.ToolId == "ffmpeg")));
                    if (!anyDirectCanEncode) filterOutput = "no";
                }

                var relevantEdges = filterOutput is not null
                    ? edges.Where(e => e.SourceOutputId == filterOutput).ToList()
                    : edges;

                if (relevantEdges.Count == 0)
                {
                    paths.Add(new(currentPath));
                }
                else
                {
                    foreach (var edge in relevantEdges)
                    {
                        Explore(edge.TargetNodeId);
                    }
                }
            }

            currentPath.RemoveAt(currentPath.Count - 1);
            visitedInCurrentPath.Remove(nodeId);
        }

        Explore(inputNode.Id);

        return paths.Select(path=>BuildRequirements(path,sourceMetadata)).ToList();
    }

    private static bool IsPureMediaCondition(ConditionExpression tree) => tree switch
    {
        ConditionGroup group => group.Children.All(IsPureMediaCondition),
        ConditionRule rule => rule.Field.StartsWith("video.", StringComparison.Ordinal)
            || rule.Field.StartsWith("audio.", StringComparison.Ordinal)
            || rule.Field.StartsWith("subtitles.", StringComparison.Ordinal)
            || rule.Field is "file.duration" or "file.size",
        _ => false
    };

    private FlowPathRequirements BuildRequirements(List<FlowNodeDefinition> nodes,JsonElement? sourceMetadata)
    {
        var nodeIds = nodes.Select(n => n.Id).ToList();
        var requiresDirect = false;
        var prefersDirect = false;
        var requiresWrite = false;
        var tools = new HashSet<string>(StringComparer.Ordinal) { "ffprobe" };
        var plugins = new HashSet<string>(StringComparer.Ordinal);
        var videoTranscodes = new List<VideoTranscodeRequirement>();
        var audioEncoders = new HashSet<string>(StringComparer.Ordinal);
        var fontSets = new HashSet<string>(StringComparer.Ordinal);
        var runConstraints = new List<NodeRunConstraint>();
        var capabilityConstraints = new List<CapabilityConstraint>();

        foreach (var node in nodes)
        {
            var component = registry.Resolve(node.Component);
            if (component?.RequiresWriteAccess == true) requiresWrite = true;

            plugins.Add("plugin/" + node.Component.PluginId);

            if (node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-require-direct-access")
            {
                if (node.Configuration.TryGetValue("mode", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "prefer")
                    prefersDirect = true;
                else
                    requiresDirect = true;
            }

            if (component is not null)
            {
                foreach (var tool in component.RequiredTools)
                {
                    tools.Add(tool.ToolId);
                    foreach (var requiredCapability in tool.RequiredCapabilities)
                        capabilityConstraints.Add(new(tool.ToolId,tool.MinimumVersion,requiredCapability,null,null));
                    if(tool.RequiredCapabilities.Count==0 && tool.MinimumVersion is not null)
                        capabilityConstraints.Add(new(tool.ToolId,tool.MinimumVersion,null,null,null));
                }
            }

            if (node.Component.PluginId == "base" && node.Component.ComponentId is "base-execute-processing" or "base-ffmpeg-builder-execute")
            {
                tools.Add("ffmpeg");
            }

            if (node.Component.ComponentId == "base-script")
            {
                var lang = node.Configuration.TryGetValue("language", out var l) ? l.GetString() : "python";
                tools.Add(lang == "javascript" ? "javascript" : "python");
                if (node.Configuration.TryGetValue("useTools", out var ut) && ut.ValueKind == JsonValueKind.True)
                    tools.Add("script-tools");
            }

            if (node.Component.ComponentId == "base-condition")
            {
                tools.Add("javascript");
            }

            if (node.Component.ComponentId is "base-transcode-video" or "video-transcode")
            {
                tools.Add("ffmpeg");
                var codec = node.Configuration.TryGetValue("videoCodec", out var cVal) ? cVal.GetString()! : "hevc";
                var mode = node.Configuration.TryGetValue("encoderMode", out var mVal) ? mVal.GetString()! : "automatic";
                var encoderId = node.Configuration.TryGetValue("encoderId", out var eVal) ? eVal.GetString() : null;
                var retryCpu = node.Configuration.TryGetValue("retryWithCpu", out var rVal) && rVal.ValueKind == JsonValueKind.True;
                var hw = node.Configuration.TryGetValue("hardwareAcceleration", out var hVal) ? hVal.GetString() : "cpu";
                EncodingProfileSnapshot? profile = null;
                if (node.Configuration.TryGetValue("encodingProfile", out var pVal) && pVal.ValueKind == JsonValueKind.Object)
                {
                    try { profile = pVal.Deserialize<EncodingProfileSnapshot>(Json); } catch { }
                }
                videoTranscodes.Add(new(codec, mode, encoderId, profile, hw, retryCpu));
            }

            if (node.Component.ComponentId is "base-audio-rules" or "audio-rules")
            {
                tools.Add("ffmpeg");
                // Audio rules are input-dependent. Before the source scan, admit a capable scanner;
                // the first metadata report replans against the exact conversions before they run.
                if(sourceMetadata is { } metadata)
                {
                    var audioPlan=AudioRulePlanner.Plan(metadata,AudioRuleOptions.From(node.Configuration));
                    foreach(var conversion in audioPlan.Conversions)
                        if(conversion.Codec is not null) audioEncoders.Add(conversion.Codec);
                }
            }
            else if (node.Component.ComponentId is "base-transcode-audio" or "audio-transcode")
            {
                tools.Add("ffmpeg");
                if (node.Configuration.TryGetValue("audioCodec", out var aCodec) && aCodec.ValueKind == JsonValueKind.String)
                    audioEncoders.Add(aCodec.GetString()!);
            }

            if (node.Component.ComponentId is "base-add-fonts" or "subtitles-add-fonts"
                || (node.Component.ComponentId == "base-assemble-media" && node.Configuration.TryGetValue("addFonts", out var af) && af.ValueKind == JsonValueKind.True))
            {
                var fromResource = node.Configuration.TryGetValue("resource", out var resource) && resource.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(resource.GetString());
                if (!fromResource)
                {
                    var fontSet = node.Configuration.TryGetValue("fontSet", out var configuredSet) && configuredSet.ValueKind == JsonValueKind.String
                        ? configuredSet.GetString()! : "Crunchyroll";
                    fontSets.Add(fontSet);
                }
            }

            if (node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-run-on-node")
            {
                Guid? tNode = null;
                if (node.Configuration.TryGetValue("targetNodeId", out var tn) && Guid.TryParse(tn.GetString(), out var tid)) tNode = tid;
                Guid? tGroup = null;
                if (node.Configuration.TryGetValue("targetGroupId", out var tg) && Guid.TryParse(tg.GetString(), out var gid)) tGroup = gid;
                var reqLabels = node.Configuration.TryGetValue("requiredLabels", out var rl) ? rl.GetString() : null;
                runConstraints.Add(new(tNode, tGroup, reqLabels));
            }

            if (node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-require-capability")
            {
                var tool = node.Configuration.TryGetValue("toolId", out var rt) ? rt.GetString() : "ffmpeg";
                var min = node.Configuration.TryGetValue("minimumVersion", out var mv) ? mv.GetString() : null;
                var reqCap = node.Configuration.TryGetValue("requiredCapability", out var rc) ? rc.GetString() : null;
                var reqEnc = node.Configuration.TryGetValue("requiredEncoder", out var re) ? re.GetString() : null;
                var kind = node.Configuration.TryGetValue("encoderKind", out var ek) ? ek.GetString() : null;
                if (tool is not null) capabilityConstraints.Add(new(tool, min, reqCap, reqEnc, kind));
            }

        }

        return new(
            nodeIds,
            requiresDirect,
            prefersDirect,
            requiresWrite,
            tools,
            plugins,
            videoTranscodes,
            audioEncoders,
            fontSets,
            runConstraints,
            capabilityConstraints);
    }

    public bool NodeSatisfiesPath(NodePlacementCandidate node, FlowPathRequirements path, out List<string> missing)
    {
        missing = [];

        if (path.RequiresDirectAccess && node.Access is not { Mode: "direct", CanRead: true, LocalPath: not null })
            missing.Add("accès direct local requis");

        if (path.RequiresWriteAccess && node.Access is not { CanWrite: true })
            missing.Add("accès en écriture requis");

        foreach (var tool in path.RequiredTools)
        {
            if (!node.Capabilities.Any(c => c.ToolId == tool))
                missing.Add($"outil manquant : {tool}");
        }

        bool HasPlugin(string pluginReq)
        {
            if (node.Capabilities.Any(c => c.ToolId == pluginReq)) return true;
            var rawId = pluginReq.StartsWith("plugin/", StringComparison.Ordinal) ? pluginReq["plugin/".Length..] : pluginReq;
            if (rawId is "video" or "audio" or "subtitles" or "mkv" or "images" or "probe")
            {
                var baseCap = node.Capabilities.FirstOrDefault(c => c.ToolId == "plugin/base");
                if (baseCap is not null && Version.TryParse(baseCap.Version, out var bv) && bv >= new Version(1, 6, 0))
                    return true;
            }
            return false;
        }

        foreach (var plugin in path.RequiredPlugins)
        {
            if (!HasPlugin(plugin))
                missing.Add($"plugin manquant : {plugin}");
        }

        if (!NodeSatisfiesTranscodeRequirements(node, path))
            missing.Add("encodeurs vidéo incompatibles");

        var ffmpeg = node.Capabilities.FirstOrDefault(c => c.ToolId == "ffmpeg");
        var availableEncoders = ffmpeg?.Encoders ?? [];
        foreach (var audio in path.AudioEncoders)
        {
            if (MediaCodecPolicy.AudioEncoder(audio, availableEncoders) is null)
                missing.Add($"encodeur audio manquant : {audio}");
        }

        foreach (var fontSet in path.FontSets)
        {
            if (node.Inventory?.FontSets?.Contains(fontSet, StringComparer.Ordinal) != true)
                missing.Add($"jeu de polices manquant : {fontSet}");
        }

        foreach (var constraint in path.RunConstraints)
        {
            if (constraint.TargetNodeId.HasValue && constraint.TargetNodeId.Value != node.NodeId)
                missing.Add($"nœud cible imposé {constraint.TargetNodeId.Value:D}");
            if (constraint.TargetGroupId.HasValue && (node.Identity is null || !node.Identity.GroupIds.Contains(constraint.TargetGroupId.Value)))
                missing.Add($"groupe imposé {constraint.TargetGroupId.Value:D}");
            if (!string.IsNullOrWhiteSpace(constraint.RequiredLabels) && !NodeLabelSelector.Matches(constraint.RequiredLabels, node.Identity?.EffectiveLabels))
                missing.Add($"étiquettes imposées {constraint.RequiredLabels}");
        }

        foreach (var cap in path.CapabilityConstraints)
        {
            var matching = node.Capabilities.FirstOrDefault(c => c.ToolId == cap.ToolId);
            if (matching is null)
            {
                missing.Add($"capacité d'outil absente : {cap.ToolId}");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(cap.MinimumVersion)
                && (!Version.TryParse(matching.Version, out var v) || !Version.TryParse(cap.MinimumVersion, out var min) || v < min))
                missing.Add($"version minimale non satisfaite pour {cap.ToolId} : >= {cap.MinimumVersion}");
            if (!string.IsNullOrWhiteSpace(cap.RequiredCapability) && !matching.AccelerationTypes.Contains(cap.RequiredCapability, StringComparer.Ordinal))
                missing.Add($"accélération requise absente : {cap.RequiredCapability}");
            if(!string.IsNullOrWhiteSpace(cap.RequiredEncoder) || cap.EncoderKind is "hardware" or "cpu")
            {
                var encoderTool=node.Capabilities.FirstOrDefault(capability=>capability.ToolId=="ffmpeg");
                var sought=cap.RequiredEncoder?.Trim();
                var encoderOptions=VideoEncoderCatalog.All.Where(encoder=>(string.IsNullOrWhiteSpace(sought)
                    || encoder.Codec.Equals(sought,StringComparison.OrdinalIgnoreCase) || encoder.Id.Equals(sought,StringComparison.OrdinalIgnoreCase))
                    && (cap.EncoderKind switch { "hardware"=>encoder.Acceleration!="cpu", "cpu"=>encoder.Acceleration=="cpu", _=>true })).ToArray();
                var usable=encoderTool?.EncoderChecks?.Where(check=>check.Status=="usable").Select(check=>check.EncoderId).ToArray();
                if(encoderTool is null || encoderOptions.Length==0 || !encoderOptions.Any(encoder=>usable?.Contains(encoder.Id,StringComparer.OrdinalIgnoreCase)
                    ?? encoderTool.Encoders?.Contains(encoder.Id,StringComparer.OrdinalIgnoreCase)==true))
                    missing.Add($"encodeur requis absent ou non utilisable : {cap.RequiredEncoder ?? cap.EncoderKind}");
            }
        }

        return missing.Count == 0;
    }

    private static bool NodeSatisfiesTranscodeRequirements(NodePlacementCandidate node, FlowPathRequirements path)
    {
        if (path.VideoTranscodes.Count == 0) return true;
        var ffmpeg = node.Capabilities.FirstOrDefault(c => c.ToolId == "ffmpeg");
        if (ffmpeg is null) return false;

        var availableEncoders = ffmpeg.Encoders ?? [];
        var accelerations = ffmpeg.AccelerationTypes ?? [];

        foreach (var transcode in path.VideoTranscodes)
        {
            if (transcode.Profile is not null)
            {
                var usable = MediaCodecPolicy.ResolveVideoEncoder(
                    transcode.Codec,
                    transcode.Mode,
                    transcode.EncoderId,
                    transcode.Profile,
                    availableEncoders,
                    accelerations,
                    ffmpeg.EncoderChecks) is not null
                    || (transcode.RetryWithCpu && MediaCodecPolicy.ResolveCpuFallbackVideoEncoder(
                        transcode.Codec,
                        transcode.Profile,
                        availableEncoders,
                        accelerations,
                        ffmpeg.EncoderChecks) is not null);
                if (!usable) return false;
            }
            else
            {
                var hw = transcode.HardwareAcceleration ?? "cpu";
                if (transcode.RetryWithCpu && hw != "cpu")
                {
                    if (MediaCodecPolicy.VideoEncoder(transcode.Codec, "cpu", availableEncoders) is null
                        && MediaCodecPolicy.VideoEncoder(transcode.Codec, hw, availableEncoders) is null)
                        return false;
                }
                else
                {
                    if (MediaCodecPolicy.VideoEncoder(transcode.Codec, hw, availableEncoders) is null)
                        return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<string> CollectCapabilitiesNames(IReadOnlyList<FlowPathRequirements> paths)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            foreach (var t in p.RequiredTools) result.Add("tool:" + t);
            foreach (var pl in p.RequiredPlugins) result.Add(pl);
            foreach (var v in p.VideoTranscodes) result.Add("video:" + v.Codec + (v.HardwareAcceleration != null ? ":" + v.HardwareAcceleration : ""));
            foreach (var a in p.AudioEncoders) result.Add("audio:" + a);
            foreach (var f in p.FontSets) result.Add("fonts:" + f);
        }
        return result.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
