using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed record AiAssistantToolPlan(string Kind, string? Content = null, string? Name = null, string? Arguments = null);
public sealed record AiAssistantActionCall(string Name, Guid ResourceId, int ExpectedVersion,
    Guid? SecondaryResourceId = null, int? SecondaryVersion = null);

/// <summary>Parses bounded read tools and explicitly enumerated action proposals. Unknown calls are rejected.</summary>
public static class AiAssistantToolPlanParser
{
    private static readonly HashSet<string> ReadTools =
        ["list_flows", "get_flow", "list_nodes", "list_executions", "get_libraries_overview"];
    private static readonly HashSet<string> Actions =
        ["publish-flow", "delete-flow", "start-library", "cancel-execution", "assign-library-flow"];

    public static AiAssistantToolPlan Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 12_000)
            throw new ArgumentException("La décision d’outil du modèle est vide ou dépasse la limite.");
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException();
            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new ArgumentException();
            var kind = root.GetProperty("kind").GetString();
            if (kind == "answer" && properties.Length == 2)
            {
                var answer = root.GetProperty("content").GetString();
                if (!string.IsNullOrWhiteSpace(answer) && answer.Length <= 8_000)
                    return new("answer", Content: answer);
            }
            if (kind == "tool_call" && properties.Length == 3
                && root.GetProperty("name").ValueKind == JsonValueKind.String
                && root.GetProperty("arguments").ValueKind == JsonValueKind.Object)
            {
                var name = root.GetProperty("name").GetString();
                if (name is not null && ReadTools.Contains(name))
                    return new("tool_call", Name: name, Arguments: root.GetProperty("arguments").GetRawText());
            }
            if (kind == "action_call" && properties.Length == 3
                && root.GetProperty("name").ValueKind == JsonValueKind.String
                && root.GetProperty("arguments").ValueKind == JsonValueKind.Object)
            {
                var name = root.GetProperty("name").GetString();
                if (name is not null && Actions.Contains(name))
                    return new("action_call", Name: name, Arguments: root.GetProperty("arguments").GetRawText());
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
        throw new ArgumentException("Le modèle a retourné une décision d’outil invalide ; aucun outil n’a été exécuté.");
    }

    public static AiAssistantToolCall ParseToolCall(AiAssistantToolPlan plan)
    {
        if (plan.Kind != "tool_call" || plan.Name is null || plan.Arguments is null || !ReadTools.Contains(plan.Name))
            throw new ArgumentException("Outil de lecture inconnu ou décision invalide.");
        using var document = JsonDocument.Parse(plan.Arguments);
        var arguments = document.RootElement;
        var keys = arguments.EnumerateObject().Select(property => property.Name).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new ArgumentException("Arguments d’outil dupliqués.");
        Guid? flowId = null;
        string? state = null;
        int? limit = null;
        foreach (var key in keys)
        {
            var value = arguments.GetProperty(key);
            switch (key)
            {
                case "flowId" when plan.Name == "get_flow" && value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(value.GetString(), out var parsedId) && parsedId != Guid.Empty:
                    flowId = parsedId;
                    break;
                case "state" when plan.Name == "list_executions" && value.ValueKind == JsonValueKind.String
                    && value.GetString() is "queued" or "running" or "failed" or "completed":
                    state = value.GetString();
                    break;
                case "limit" when plan.Name == "list_executions" && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var parsedLimit) && parsedLimit is >= 1 and <= 50:
                    limit = parsedLimit;
                    break;
                default:
                    throw new ArgumentException("Argument d’outil inconnu ou invalide.");
            }
        }

        if (plan.Name == "get_flow" && flowId is null
            || plan.Name != "get_flow" && flowId is not null
            || plan.Name != "list_executions" && (state is not null || limit is not null))
            throw new ArgumentException("Les arguments ne correspondent pas au contrat de l’outil.");
        return new(plan.Name, flowId, state, limit);
    }

    public static AiAssistantActionCall ParseActionCall(AiAssistantToolPlan plan)
    {
        if (plan.Kind != "action_call" || plan.Name is null || plan.Arguments is null || !Actions.Contains(plan.Name))
            throw new ArgumentException("Action inconnue ou décision invalide.");
        using var document = JsonDocument.Parse(plan.Arguments);
        var arguments = document.RootElement;
        var keys = arguments.EnumerateObject().Select(property => property.Name).ToArray();
        var assignment = plan.Name == "assign-library-flow";
        var expectedKeys = assignment
            ? new[] { "resourceId", "expectedVersion", "flowId", "flowVersion" }
            : new[] { "resourceId", "expectedVersion" };
        if (keys.Length != expectedKeys.Length || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length
            || expectedKeys.Any(key => !keys.Contains(key, StringComparer.Ordinal)))
            throw new ArgumentException("Arguments d’action invalides.");
        var resourceValue = arguments.GetProperty("resourceId");
        var versionValue = arguments.GetProperty("expectedVersion");
        if (resourceValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(resourceValue.GetString(), out var resourceId) || resourceId == Guid.Empty
            || versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out var version) || version < 0)
            throw new ArgumentException("Identifiant ou version d’action invalide.");
        if (!assignment) return new(plan.Name, resourceId, version);
        var flowIdValue = arguments.GetProperty("flowId");
        var flowVersionValue = arguments.GetProperty("flowVersion");
        if (flowIdValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(flowIdValue.GetString(), out var flowId) || flowId == Guid.Empty
            || flowVersionValue.ValueKind != JsonValueKind.Number
            || !flowVersionValue.TryGetInt32(out var flowVersion) || flowVersion < 1)
            throw new ArgumentException("Flow ou version publiée invalide.");
        return new(plan.Name, resourceId, version, flowId, flowVersion);
    }
}
