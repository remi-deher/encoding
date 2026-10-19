using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed record AiAssistantToolPlan(string Kind, string? Content = null, string? Name = null, string? Arguments = null);

/// <summary>Parses the model's bounded read-only tool protocol. Unknown tools and arguments are rejected.</summary>
public static class AiAssistantToolPlanParser
{
    private static readonly HashSet<string> ReadTools =
        ["list_flows", "get_flow", "list_nodes", "list_executions", "get_libraries_overview"];

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
}
