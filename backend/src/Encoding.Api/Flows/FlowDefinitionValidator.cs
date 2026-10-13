using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Flows;

public sealed class FlowDefinitionValidator(FlowComponentRegistry registry)
{
    private static readonly JsonSerializerOptions DocumentOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public (FlowDefinition? Definition, FlowValidationResult Result) ValidateDocument(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object || document.GetRawText().Length > 1048576)
            return (null, new([new("document.invalid", "/", "La définition doit être un objet JSON de moins de 1 Mo.")]));
        try
        {
            var definition = document.Deserialize<FlowDefinition>(DocumentOptions);
            return definition is null
                ? (null, new([new("document.required", "/", "Définition obligatoire.")]))
                : (definition, Validate(definition));
        }
        catch (JsonException)
        {
            return (null, new([new("document.invalid", "/", "Le document ne respecte pas le contrat de définition de flow.")]));
        }
    }

    public FlowValidationResult Validate(FlowDefinition flow)
    {
        var issues = new List<FlowValidationIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));
        if (flow.FormatVersion != 1) Issue("format.unsupported", "/formatVersion", "Format de flow non pris en charge.");
        if (flow.Id == Guid.Empty) Issue("flow.id.required", "/id", "L’identifiant est obligatoire.");
        if (flow.Version <= 0) Issue("flow.version.invalid", "/version", "La version doit être positive.");
        if (string.IsNullOrWhiteSpace(flow.Name) || flow.Name.Length > 128) Issue("flow.name.invalid", "/name", "Le nom doit contenir entre 1 et 128 caractères.");
        if (flow.MediaType is not ("video" or "image" or "music" or "subtitles")) Issue("flow.media.invalid", "/mediaType", "Type de média inconnu.");
        if (flow.Nodes is null || flow.Connections is null)
        {
            Issue("graph.required", "/", "Les listes nodes et connections sont obligatoires.");
            return new(issues);
        }
        if (flow.Nodes.Count > 1000 || flow.Connections.Count > 4000)
        {
            Issue("graph.tooLarge", "/", "Maximum : 1000 blocs et 4000 connexions.");
            return new(issues);
        }
        var nodes = new Dictionary<string, FlowComponentDescriptor>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < flow.Nodes.Count; i++)
        {
            var node = flow.Nodes[i];
            var path = $"/nodes/{i}";
            if (node is null || string.IsNullOrWhiteSpace(node.Id)) { Issue("node.id.required", path + "/id", "Identifiant de bloc obligatoire."); continue; }
            if (!ids.Add(node.Id)) { Issue("node.id.duplicate", path + "/id", "Identifiant de bloc dupliqué."); continue; }
            var component = node.Component is null ? null : registry.Resolve(node.Component);
            if (component is null) { Issue("component.unavailable", path + "/component", "Cette version du composant, du plugin ou du schéma n’est pas disponible."); continue; }
            nodes.Add(node.Id, component);
            if (!component.SupportedFlowMediaTypes.Contains(flow.MediaType, StringComparer.Ordinal)) Issue("component.incompatible", path + "/component", "Composant incompatible avec le média du flow.");
            if (node.Configuration is null) { Issue("configuration.required", path + "/configuration", "La configuration doit être un objet."); continue; }
            foreach (var key in node.Configuration.Keys)
                if (!component.ConfigurationSchema.Fields.Any(f => f.Id == key)) Issue("field.unknown", path + "/configuration/" + Pointer(key), "Champ inconnu.");
            foreach (var field in component.ConfigurationSchema.Fields)
            {
                node.Configuration.TryGetValue(field.Id, out var value);
                var message = ValidateField(field, value, flow.MediaType);
                if (message is not null) Issue("field.invalid", path + "/configuration/" + Pointer(field.Id), message);
            }
        }
        var entries = nodes.Where(n => n.Value.Kind == "input").Select(n => n.Key).ToArray();
        if (entries.Length != 1) Issue("graph.entry.count", "/nodes", "Le flow doit posséder exactement une entrée.");
        var outgoing = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var indegree = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var outputs = new HashSet<(string, string)>();
        for (var i = 0; i < flow.Connections.Count; i++)
        {
            var edge = flow.Connections[i];
            var path = $"/connections/{i}";
            if (edge is null) { Issue("connection.required", path, "Connexion obligatoire."); continue; }
            if (string.IsNullOrWhiteSpace(edge.Id) || !edgeIds.Add(edge.Id)) Issue("connection.id.invalid", path + "/id", "Identifiant obligatoire et unique.");
            if (edge.SourceNodeId is null || edge.TargetNodeId is null || !ids.Contains(edge.SourceNodeId) || !ids.Contains(edge.TargetNodeId))
            { Issue("connection.node.unknown", path, "La connexion référence un bloc inconnu."); continue; }
            if (edge.SourceNodeId == edge.TargetNodeId) Issue("connection.self", path, "Un bloc ne peut pas se relier à lui-même.");
            if (nodes.TryGetValue(edge.SourceNodeId, out var source) && !source.Outputs.Any(o => o.Id == edge.SourceOutputId)) Issue("connection.output.unknown", path + "/sourceOutputId", "Sortie non déclarée.");
            if (nodes.TryGetValue(edge.TargetNodeId, out var target) && target.Kind == "input") Issue("connection.entry", path + "/targetNodeId", "L’entrée du flow ne reçoit pas de liaison.");
            if (!outputs.Add((edge.SourceNodeId, edge.SourceOutputId))) Issue("connection.output.duplicate", path, "Une sortie ne peut être reliée qu’une fois.");
            outgoing[edge.SourceNodeId].Add(edge.TargetNodeId);
            indegree[edge.TargetNodeId]++;
        }
        var queue = new Queue<string>(indegree.Where(p => p.Value == 0).Select(p => p.Key));
        var count = 0;
        while (queue.TryDequeue(out var id))
        {
            count++;
            foreach (var target in outgoing[id]) if (--indegree[target] == 0) queue.Enqueue(target);
        }
        if (count != ids.Count) Issue("graph.cycle", "/connections", "Les cycles libres sont interdits.");
        if (entries.Length == 1)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(); pending.Push(entries[0]);
            while (pending.TryPop(out var id)) if (seen.Add(id)) foreach (var target in outgoing[id]) pending.Push(target);
            foreach (var id in ids) if (!seen.Contains(id)) Issue("node.unreachable", "/nodes", $"Bloc non relié à l’entrée : {id}.");
        }
        return new(issues);
    }

    private static string Pointer(string key) => key.Replace("~", "~0").Replace("/", "~1");

    private static string? ValidateField(FlowConfigurationField field, JsonElement value, string mediaType)
    {
        var empty = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString());
        if (empty) return field.Required ? "Ce paramètre est obligatoire." : null;
        switch (field.Type)
        {
            case "text": return value.ValueKind == JsonValueKind.String ? null : "Une valeur textuelle est attendue.";
            case "boolean": return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Une valeur Oui ou Non est attendue.";
            case "select": return value.ValueKind == JsonValueKind.String && (field.Options ?? []).Any(o => o.Value == value.GetString() && (o.SupportedFlowMediaTypes is null || o.SupportedFlowMediaTypes.Contains(mediaType))) ? null : "Choisissez une valeur proposée pour ce média.";
            case "number":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)) return "Un nombre valide est attendu.";
                if (field.Min is { } min && number < min) return $"Minimum : {min}.";
                if (field.Max is { } max && number > max) return $"Maximum : {max}.";
                if (field.Step is > 0)
                {
                    try
                    {
                        var increment = (number - (field.Min ?? 0)) / field.Step.Value;
                        if (Math.Abs(increment - Math.Round(increment)) > 0.00000001m) return $"Pas attendu : {field.Step}.";
                    }
                    catch (OverflowException) { return "Le nombre dépasse la plage prise en charge."; }
                }
                return null;
            default: return "Type de champ non pris en charge.";
        }
    }
}
