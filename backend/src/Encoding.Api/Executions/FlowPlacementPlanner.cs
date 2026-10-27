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
    LibraryNodeAccessInfo? Access);

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
        var paths = ExtractPathRequirements(flow, sourceMetadata);
        if (paths.Count == 0)
        {
            return new(
                FlowPlacementPlan.StrategyImpossible,
                null, null, null,
                "Placement impossible : aucun chemin d’exécution valide dans le flow.",
                [], ["aucun_chemin_valide"], now);
        }

        var allRequiredCaps = CollectCapabilitiesNames(paths);

        // 1. Nœuds d'accès direct en ligne
        var directAccessNodes = candidates
            .Where(c => c.IsOnline && c.IsWorker && c.Access is { IsEnabled: true, Mode: "direct", CanRead: true, LocalPath: not null })
            .ToList();

        // Cas 1 : Priorité 1 — Un nœud d'accès direct peut exécuter tous les chemins applicables
        foreach (var directNode in directAccessNodes)
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

        // Cas 2 : Priorité 2 — Plan regroupé (TranscodeRelay)
        // Le nœud d'accès direct gère la préparation et la publication, un autre nœud worker gère le transcodage
        var directWriteNodes = directAccessNodes.Where(c => c.Access is { CanWrite: true }).ToList();
        if (directWriteNodes.Count > 0)
        {
            var accessNode = directWriteNodes[0];
            var needsTranscode = paths.Any(p => p.VideoTranscodes.Count > 0);

            if (needsTranscode)
            {
                var transcodeCandidates = candidates
                    .Where(c => c.IsOnline && c.IsWorker && c.NodeId != accessNode.NodeId
                        && c.Access is { IsEnabled: true, CanRead: true })
                    .ToList();

                foreach (var transcodeNode in transcodeCandidates)
                {
                    var canTranscode = paths.All(p => NodeSatisfiesTranscodeRequirements(transcodeNode, p));
                    if (canTranscode)
                    {
                        var accessLabel = accessNode.Name ?? accessNode.NodeId.ToString("D");
                        var transcodeLabel = transcodeNode.Name ?? transcodeNode.NodeId.ToString("D");
                        return new(
                            FlowPlacementPlan.StrategyTranscodeRelay,
                            accessNode.NodeId,
                            accessNode.NodeId,
                            transcodeNode.NodeId,
                            $"Placement regroupé : {accessLabel} (préparation) → {transcodeLabel} (encodage) → {accessLabel} (assemblage/publication), 1 transfert aller-retour.",
                            allRequiredCaps,
                            [],
                            now);
                    }
                }
            }
        }

        // Cas 3 : Un worker avec accès par transfert peut faire tout le flow
        var transferWorkers = candidates
            .Where(c => c.IsOnline && c.IsWorker && c.Access is { IsEnabled: true, CanRead: true })
            .ToList();

        foreach (var worker in transferWorkers)
        {
            if (paths.All(p => (!p.RequiresWriteAccess || worker.Access!.CanWrite) && NodeSatisfiesPath(worker, p, out _)))
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
                    now);
            }
        }

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

    public IReadOnlyList<FlowPathRequirements> ExtractPathRequirements(FlowDefinition flow, JsonElement? sourceMetadata)
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

        return paths.Select(BuildRequirements).ToList();
    }

    private FlowPathRequirements BuildRequirements(List<FlowNodeDefinition> nodes)
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
                foreach (var tool in component.RequiredTools) tools.Add(tool.ToolId);
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
                audioEncoders.Add("aac");
                audioEncoders.Add("ac3");
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
                var fontSet = node.Configuration.TryGetValue("fontSet", out var configuredSet) && configuredSet.ValueKind == JsonValueKind.String
                    ? configuredSet.GetString()! : "Crunchyroll";
                fontSets.Add(fontSet);
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
            if (!string.IsNullOrWhiteSpace(cap.MinimumVersion) && Version.TryParse(matching.Version, out var v) && Version.TryParse(cap.MinimumVersion, out var min) && v < min)
                missing.Add($"version minimale non satisfaite pour {cap.ToolId} : >= {cap.MinimumVersion}");
            if (!string.IsNullOrWhiteSpace(cap.RequiredCapability) && !matching.AccelerationTypes.Contains(cap.RequiredCapability, StringComparer.Ordinal))
                missing.Add($"accélération requise absente : {cap.RequiredCapability}");
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
