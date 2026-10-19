using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Encoding.Api.Tools;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Flows;

public sealed class FlowAiGeneratorService(
    AiClientFactory clientFactory,
    FlowCatalogPromptBuilder promptBuilder,
    FlowDefinitionValidator validator)
{
    public async Task<FlowAiResponse> GenerateFlowAsync(FlowAiGenerateRequest request, CancellationToken ct = default, bool localOnly = false)
    {
        var client = clientFactory.CreateClient(request.Provider, localOnly);
        var systemPrompt = promptBuilder.BuildSystemPrompt(request.MediaType);
        var userPrompt = $"Génère un flow complet pour le média '{request.MediaType}' répondant à cette demande :\n\"{request.UserPrompt}\"";

        var aiResult = await client.GenerateTextAsync(new AiPrompt(systemPrompt, userPrompt, SessionId: request.SessionId, RequestId: request.RequestId), ct);
        FlowAiResponse Reply(FlowAiResponse response) => response with { SessionId = aiResult.SessionId, RequestId = aiResult.RequestId };
        if (!aiResult.Success)
        {
            return Reply(new FlowAiResponse(false, null, aiResult.ErrorMessage));
        }

        var json = ExtractJson(aiResult.Content);
        if (json is null)
        {
            return Reply(new FlowAiResponse(false, null, "L’IA n’a pas retourné de JSON valide."));
        }

        var (def, val) = TryValidate(json);
        if (val is not null && val.IsValid && def is not null)
        {
            return Reply(new FlowAiResponse(true, def));
        }

        // Boucle d'auto-réparation (1 tour)
        var issuesText = string.Join("\n", val?.Issues.Select(i => $"- [{i.Code}] {i.Path}: {i.Message}") ?? []);
        var repairResult = await RepairAsync(client, systemPrompt, aiResult, json, issuesText, ct);
        if (repairResult.Success)
        {
            var repairedJson = ExtractJson(repairResult.Content);
            if (repairedJson is not null)
            {
                var (repDef, repVal) = TryValidate(repairedJson);
                if (repVal is not null && repVal.IsValid && repDef is not null)
                {
                    return Reply(new FlowAiResponse(true, repDef));
                }
            }
        }

        return Reply(new FlowAiResponse(false, null, "Le flow généré ne respecte pas le catalogue après une tentative de réparation.", val?.Issues));
    }

    public async Task<FlowAiResponse> RefactorFlowAsync(FlowAiRefactorRequest request, CancellationToken ct = default, bool localOnly = false)
    {
        var client = clientFactory.CreateClient(request.Provider, localOnly);
        var currentJson = JsonSerializer.Serialize(request.CurrentFlow, new JsonSerializerOptions { WriteIndented = true });
        var systemPrompt = promptBuilder.BuildSystemPrompt(request.CurrentFlow.MediaType);
        var userPrompt = $"Modifie ce flow existant selon l'instruction suivante : \"{request.UserPrompt}\".\n\nFlow actuel :\n{currentJson}";

        var aiResult = await client.GenerateTextAsync(new AiPrompt(systemPrompt, userPrompt, SessionId: request.SessionId, RequestId: request.RequestId), ct);
        FlowAiResponse Reply(FlowAiResponse response) => response with { SessionId = aiResult.SessionId, RequestId = aiResult.RequestId };
        if (!aiResult.Success)
        {
            return Reply(new FlowAiResponse(false, null, aiResult.ErrorMessage));
        }

        var json = ExtractJson(aiResult.Content);
        if (json is null)
        {
            return Reply(new FlowAiResponse(false, null, "L'IA n'a pas retourné de JSON valide."));
        }

        var (def, val) = TryValidate(json);
        if (val is not null && val.IsValid && def is not null)
        {
            def = PreserveRefactorIdentity(def, request.CurrentFlow);
            var (_, identityValidation) = TryValidate(JsonSerializer.Serialize(def));
            if (identityValidation is { IsValid: true }) return Reply(new FlowAiResponse(true, def));
            val = identityValidation;
        }

        var issuesText = string.Join("\n", val?.Issues.Select(i => $"- [{i.Code}] {i.Path}: {i.Message}") ?? []);
        var repairResult = await RepairAsync(client, systemPrompt, aiResult, json, issuesText, ct);
        if (repairResult.Success)
        {
            var repairedJson = ExtractJson(repairResult.Content);
            if (repairedJson is not null)
            {
                var (repDef, repVal) = TryValidate(repairedJson);
                if (repVal is not null && repVal.IsValid && repDef is not null)
                {
                    repDef = PreserveRefactorIdentity(repDef, request.CurrentFlow);
                    var (_, identityValidation) = TryValidate(JsonSerializer.Serialize(repDef));
                    if (identityValidation is { IsValid: true }) return Reply(new FlowAiResponse(true, repDef));
                    val = identityValidation;
                }
            }
        }

        return Reply(new FlowAiResponse(false, def, "Le flow modifié ne respecte pas le catalogue après une tentative de réparation.", val?.Issues));
    }

    public async Task<FlowAiResponse> ExplainFlowAsync(FlowAiExplainRequest request, CancellationToken ct = default, bool localOnly = false)
    {
        var client = clientFactory.CreateClient(request.Provider, localOnly);
        var currentJson = JsonSerializer.Serialize(request.Flow, new JsonSerializerOptions { WriteIndented = true });
        var systemPrompt = "Tu es un assistant expert qui explique avec pédagogie et précision le fonctionnement d'un flow de traitement de fichiers multimédias dans Encoding.";
        var userPrompt = $"Explique étape par étape ce que fait ce flow (objectifs, conditions, transcodage, gestion des pistes) en français structuré :\n\n{currentJson}";

        var aiResult = await client.GenerateTextAsync(new AiPrompt(systemPrompt, userPrompt, SessionId: request.SessionId, RequestId: request.RequestId), ct);
        return new FlowAiResponse(aiResult.Success, request.Flow, aiResult.Success ? aiResult.Content : aiResult.ErrorMessage, SessionId: aiResult.SessionId, RequestId: aiResult.RequestId);
    }

    private (FlowDefinition? Definition, FlowValidationResult? Result) TryValidate(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return validator.ValidateDocument(doc.RootElement);
        }
        catch
        {
            return (null, null);
        }
    }

    private static Task<AiCompletionResult> RepairAsync(
        IAiTextClient client,
        string systemPrompt,
        AiCompletionResult original,
        string invalidJson,
        string issuesText,
        CancellationToken ct)
    {
        var repairPrompt = $"Le JSON précédent contient les erreurs suivantes :\n{issuesText}\n\nCorrige le document JSON et renvoie UNIQUEMENT l'objet JSON valide sans texte additionnel :\n{invalidJson}";
        var operationId = original.RequestId ?? Guid.NewGuid();
        var repairId = CreateRepairRequestId(operationId);
        return client.GenerateTextAsync(new AiPrompt(systemPrompt, repairPrompt,
            SessionId: original.SessionId, RequestId: repairId), ct);
    }

    private static Guid CreateRepairRequestId(Guid operationId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"flow-repair-v1:{operationId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static FlowDefinition PreserveRefactorIdentity(FlowDefinition proposed, FlowDefinition original)
    {
        var originalById = original.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var proposedIds = proposed.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var remappedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = proposed.Nodes.Select(node =>
        {
            if (originalById.ContainsKey(node.Id))
            {
                usedIds.Add(node.Id);
                remappedIds[node.Id] = node.Id;
                return node;
            }
            var match = FindUniqueUnchangedNode(node, original.Nodes, usedIds, proposedIds);
            var id = match?.Id ?? node.Id;
            usedIds.Add(id);
            remappedIds[node.Id] = id;
            return node with { Id = id };
        }).ToArray();

        var proposedConnections = proposed.Connections.Select(connection => connection with
        {
            SourceNodeId = remappedIds.GetValueOrDefault(connection.SourceNodeId, connection.SourceNodeId),
            TargetNodeId = remappedIds.GetValueOrDefault(connection.TargetNodeId, connection.TargetNodeId),
        }).ToArray();
        var originalConnections = original.Connections.ToList();
        var preservedConnections = proposedConnections.Select(connection =>
        {
            var match = originalConnections.FirstOrDefault(candidate => SameConnection(candidate, connection));
            if (match is null) return connection;
            originalConnections.Remove(match);
            return connection with { Id = match.Id };
        }).ToArray();

        var packs = (proposed.ComponentPacks ?? []).ToList();
        var currentNodeIds = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var proposedById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var pack in original.ComponentPacks ?? [])
        {
            if (packs.Any(candidate => candidate.Id == pack.Id)
                || pack.NodeIds.Any(nodeId => !currentNodeIds.Contains(nodeId)
                    || !proposedById.TryGetValue(nodeId, out var proposedNode)
                    || !originalById.TryGetValue(nodeId, out var originalNode)
                    || !SameNodeContent(proposedNode, originalNode))) continue;
            packs.Add(pack);
        }

        return proposed with
        {
            Id = original.Id,
            Version = original.Version,
            MediaType = original.MediaType,
            Nodes = nodes,
            Connections = preservedConnections,
            ComponentPacks = packs,
        };
    }

    private static FlowNodeDefinition? FindUniqueUnchangedNode(
        FlowNodeDefinition proposed,
        IReadOnlyList<FlowNodeDefinition> originals,
        HashSet<string> usedIds,
        HashSet<string> proposedIds)
    {
        var matches = originals.Where(candidate => !usedIds.Contains(candidate.Id)
            && !proposedIds.Contains(candidate.Id)
            && SameNodeContent(proposed, candidate)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool SameNodeContent(FlowNodeDefinition left, FlowNodeDefinition right) =>
        left.Name == right.Name
        && left.Component == right.Component
        && left.Configuration.Count == right.Configuration.Count
        && left.Configuration.All(pair => right.Configuration.TryGetValue(pair.Key, out var value)
            && JsonNode.DeepEquals(JsonNode.Parse(pair.Value.GetRawText()), JsonNode.Parse(value.GetRawText())));

    private static bool SameConnection(FlowConnectionDefinition left, FlowConnectionDefinition right) =>
        left.SourceNodeId == right.SourceNodeId
        && left.SourceOutputId == right.SourceOutputId
        && left.TargetNodeId == right.TargetNodeId;

    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var fenceMatch = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success) return fenceMatch.Groups[1].Value.Trim();

        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return text.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return null;
    }

}
