using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Contracts.Executions;

/// <summary>Condition de réveil résolue d'un bloc Attendre (architecture 0033). Valeurs simples pour une égalité exacte entre worker et serveur.</summary>
public sealed record RuntimeWait(string Mode, int? DelayMinutes = null, Guid? NodeId = null, string? NodeStatus = null,
    string? Metric = null, string? Scope = null, string? Operator = null, int? Threshold = null,
    string? BlockNodeId = null, string? MetOutputs = null, int PollSeconds = 60, int? MaxWaitMinutes = null)
{
    public bool IsServerEvaluated => Mode != "block";
}

/// <summary>Sonde demandée à un worker : exécuter une condition de plugin du flow, sans créer d'étape.</summary>
public sealed record RuntimeWaitProbe(string BlockNodeId, string MetOutputs)
{
    public IReadOnlySet<string> Met => new HashSet<string>(MetOutputs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
}

public sealed record ReportWaitProbe(long Epoch, string? OutputId);

/// <summary>Interprétation commune de la configuration du bloc Attendre.</summary>
public static class RuntimeWaitPolicy
{
    public const string PluginId = "base";
    public const string ComponentId = "base-wait";
    public static readonly string[] Modes = ["timer", "nodeState", "workload", "block"];

    public static bool IsWait(FlowNodeDefinition node) => node.Component.PluginId == PluginId && node.Component.ComponentId == ComponentId;

    public static RuntimeWait Resolve(FlowNodeDefinition node)
    {
        var c = node.Configuration;
        string? Text(string key) => c.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
        int? Number(string key, int min, int max)
        {
            if (!c.TryGetValue(key, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var d) || d != decimal.Truncate(d) || d < min || d > max)
                throw new ArgumentException($"Le paramètre {key} doit être un entier entre {min} et {max}.");
            return (int)d;
        }
        var mode = Text("mode") ?? "timer";
        if (!Modes.Contains(mode)) throw new ArgumentException("Condition de réveil inconnue.");
        if (Text("mediaPolicy") is { } policy && policy != "keep") throw new ArgumentException("Seule la conservation du média réservé est disponible.");
        var poll = Number("pollSeconds", 15, 3600) ?? 60;
        var max = Number("maxWaitMinutes", 1, 43200);
        switch (mode)
        {
            case "timer":
                return new(mode, DelayMinutes: Number("delayMinutes", 1, 10080) ?? 30, PollSeconds: poll, MaxWaitMinutes: max);
            case "nodeState":
            {
                var status = Text("nodeStatus") ?? "online";
                if (status is not ("online" or "offline" or "idle")) throw new ArgumentException("État de worker inconnu.");
                Guid? target = null;
                if (Text("nodeId") is { } raw)
                {
                    if (!Guid.TryParse(raw, out var id) || id == Guid.Empty) throw new ArgumentException("Identifiant de worker invalide.");
                    target = id;
                }
                if (status == "offline" && target is null) throw new ArgumentException("Attendre qu'un worker soit hors ligne nécessite de le désigner.");
                return new(mode, NodeId: target, NodeStatus: status, PollSeconds: poll, MaxWaitMinutes: max);
            }
            case "workload":
            {
                var metric = Text("workloadMetric") ?? "running"; var scope = Text("workloadScope") ?? "cluster"; var op = Text("workloadOperator") ?? "lte";
                if (metric is not ("running" or "queued") || scope is not ("cluster" or "library") || op is not ("lte" or "gte"))
                    throw new ArgumentException("Charge de travail : mesure, portée ou comparaison inconnue.");
                return new(mode, Metric: metric, Scope: scope, Operator: op, Threshold: Number("workloadThreshold", 0, 100000) ?? 0, PollSeconds: poll, MaxWaitMinutes: max);
            }
            default:
            {
                var block = Text("blockNodeId") ?? throw new ArgumentException("Désignez le bloc à surveiller.");
                var outputs = (Text("blockOutputs") ?? throw new ArgumentException("Indiquez les sorties du bloc qui valent « condition remplie »."))
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (outputs.Length == 0) throw new ArgumentException("Indiquez les sorties du bloc qui valent « condition remplie ».");
                return new(mode, BlockNodeId: block, MetOutputs: string.Join(',', outputs), PollSeconds: poll, MaxWaitMinutes: max);
            }
        }
    }

    /// <summary>Sortie réellement utilisée à l'expiration : « timeout » si elle est reliée, sinon un échec explicite.</summary>
    public static (string OutputId, string? NextNodeId) TimeoutRoute(FlowDefinition flow, string nodeId)
    {
        var next = FlowPath.NextNodeId(flow, nodeId, "timeout");
        return next is null ? ("error", null) : ("timeout", next);
    }
}
