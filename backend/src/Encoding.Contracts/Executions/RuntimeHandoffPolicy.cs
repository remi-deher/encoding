using System.Text.Json;
using Encoding.Contracts.Flows;

using Encoding.Contracts.Nodes;

namespace Encoding.Contracts.Executions;

public sealed record RuntimeHandoffSelector(string Mode, Guid? TargetNodeId = null, Guid? TargetGroupId = null, string? RequiredLabels = null)
{
    public bool Accepts(Guid candidate, Guid previousWorker, NodeIdentityResponse? identity = null) => candidate != previousWorker
        && (Mode == "eligible" || Mode == "node" && TargetNodeId == candidate
            || Mode == "selector" && identity is not null
                && (TargetGroupId is null || identity.GroupIds.Contains(TargetGroupId.Value))
                && NodeLabelSelector.Matches(RequiredLabels, identity.EffectiveLabels));
}

/// <summary>Shared interpretation of defaults, flow overrides and the explicit relay block.</summary>
public static class RuntimeHandoffPolicy
{
    public static bool IsHandoff(FlowNodeDefinition node) => node.Component.PluginId == "nodes"
        && node.Component.ComponentId == "nodes-handoff";

    public static RuntimeHandoffSelector Resolve(FlowNodeDefinition node,
        IReadOnlyDictionary<string, JsonElement>? pluginConfiguration)
    {
        string? Read(string key)
        {
            if (node.Configuration.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString() != "inherit") return value.GetString();
            return pluginConfiguration?.TryGetValue(key, out value) == true && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        var mode = Read("mode") ?? "eligible";
        if (mode == "eligible") return new(mode);
        if (mode == "node")
        {
            if (!Guid.TryParse(Read("targetNodeId"), out var target) || target == Guid.Empty)
                throw new ArgumentException("Le relais vers un nœud précis nécessite un identifiant de worker valide.");
            return new(mode, target);
        }
        if (mode == "selector")
        {
            var groupValue = Read("targetGroupId");
            Guid? group = null;
            if (!string.IsNullOrWhiteSpace(groupValue))
            {
                if (!Guid.TryParse(groupValue, out var groupId) || groupId == Guid.Empty)
                    throw new ArgumentException("L’identifiant du groupe destinataire est invalide.");
                group = groupId;
            }
            var labels = Read("requiredLabels");
            if (!string.IsNullOrWhiteSpace(labels) && !NodeLabelSelector.IsValid(labels))
                throw new ArgumentException("Le filtre d’étiquettes du relais est invalide.");
            if (group is null && string.IsNullOrWhiteSpace(labels))
                throw new ArgumentException("Le filtre de relais doit définir un groupe ou des étiquettes.");
            return new(mode, TargetGroupId: group, RequiredLabels: labels);
        }
        throw new ArgumentException("La destination du relais n’est pas prise en charge.");
    }

    /// <summary>All possible sequential paths in this segment; stop at the next relay boundary.</summary>
    public static bool IsLibraryLocal(FlowNodeDefinition node) => node.Component.PluginId == "nodes"
        && node.Component.ComponentId == "nodes-library-local";

    /// <summary>
    /// <paramref name="libraryLocal"/> known: only the branch the worker will really follow after a "library on this node" block is kept;
    /// unknown (<c>null</c>): both branches are kept.
    /// </summary>
    public static IReadOnlyList<FlowNodeDefinition> Segment(FlowDefinition flow, string entryNodeId, bool? libraryLocal = null)
    {
        var nodes = flow.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var outgoing = flow.Connections.ToLookup(edge => edge.SourceNodeId);
        var pending = new Stack<string>(); pending.Push(entryNodeId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FlowNodeDefinition>();
        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id)) continue;
            var node = nodes[id]; result.Add(node);
            if (IsHandoff(node)) continue;
            foreach (var edge in outgoing[id])
            {
                if (libraryLocal is { } local && IsLibraryLocal(node) && edge.SourceOutputId != (local ? "yes" : "no")) continue;
                pending.Push(edge.TargetNodeId);
            }
        }
        return result;
    }
}
