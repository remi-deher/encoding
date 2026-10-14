using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Contracts.Executions;

public sealed record RuntimeHandoffSelector(string Mode, Guid? TargetNodeId = null)
{
    public bool Accepts(Guid candidate, Guid previousWorker) => candidate != previousWorker
        && (Mode == "eligible" || Mode == "node" && TargetNodeId == candidate);
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
        if (mode != "node" || !Guid.TryParse(Read("targetNodeId"), out var target) || target == Guid.Empty)
            throw new ArgumentException("Le relais vers un nœud précis nécessite un identifiant de worker valide.");
        return new(mode, target);
    }

    /// <summary>All possible sequential paths in this segment; stop at the next relay boundary.</summary>
    public static IReadOnlyList<FlowNodeDefinition> Segment(FlowDefinition flow, string entryNodeId)
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
            foreach (var edge in outgoing[id]) pending.Push(edge.TargetNodeId);
        }
        return result;
    }
}
