using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Contracts.Text;

namespace Encoding.Contracts.Flows;

public static class FlowDefinitionText
{
    /// <summary>Corrige les accents cassés (UTF-8 relu en Windows-1252) dans les noms, libellés et textes de configuration.</summary>
    public static FlowDefinition RepairText(this FlowDefinition flow) => flow with
    {
        Name = Mojibake.Repair(flow.Name)!,
        Nodes = [.. flow.Nodes.Select(node => node with
        {
            Name = Mojibake.Repair(node.Name),
            Configuration = node.Configuration.ToDictionary(pair => pair.Key, pair => RepairElement(pair.Value)),
        })],
        ComponentPacks = flow.ComponentPacks is null ? null : [.. flow.ComponentPacks.Select(pack => pack with
        {
            Name = Mojibake.Repair(pack.Name)!,
            Outputs = [.. pack.Outputs.Select(output => output with { Label = Mojibake.Repair(output.Label)! })],
        })],
    };

    private static JsonElement RepairElement(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText());
        return Repair(node) is { } repaired ? JsonSerializer.SerializeToElement(repaired) : element;
    }

    private static JsonNode? Repair(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Mojibake.Repair(text));
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList()) obj[key] = Repair(obj[key]);
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) array[i] = Repair(array[i]);
                return array;
            default:
                return node;
        }
    }
}
