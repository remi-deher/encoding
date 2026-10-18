using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Tools;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Flows;

public sealed class FlowAiGeneratorService(
    AiClientFactory clientFactory,
    FlowCatalogPromptBuilder promptBuilder,
    FlowDefinitionValidator validator)
{
    public async Task<FlowAiResponse> GenerateFlowAsync(FlowAiGenerateRequest request, CancellationToken ct = default)
    {
        var client = clientFactory.CreateClient(request.Provider);
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
        var repairPrompt = $"Le JSON précédent contient les erreurs suivantes :\n{issuesText}\n\nCorrige le document JSON et renvoie UNIQUEMENT l'objet JSON valide sans texte additionnel :\n{json}";

        var repairResult = await client.GenerateTextAsync(new AiPrompt(systemPrompt, repairPrompt), ct);
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

        return Reply(new FlowAiResponse(false, null, "Le flow généré ne respecte pas le catalogue.", val?.Issues));
    }

    public async Task<FlowAiResponse> RefactorFlowAsync(FlowAiRefactorRequest request, CancellationToken ct = default)
    {
        var client = clientFactory.CreateClient(request.Provider);
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
            return Reply(new FlowAiResponse(true, def));
        }

        return Reply(new FlowAiResponse(false, def, "Le flow modifié contient des erreurs de validation.", val?.Issues));
    }

    public async Task<FlowAiResponse> ExplainFlowAsync(FlowAiExplainRequest request, CancellationToken ct = default)
    {
        var client = clientFactory.CreateClient(request.Provider);
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
