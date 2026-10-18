using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class OpenAiCompatibleAiClient(HttpClient httpClient, string model = "gpt-4o-mini", string? apiKey = null) : IAiTextClient
{
    public async Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default)
    {
        try
        {
            var requestBody = new
            {
                model = prompt.Model ?? model,
                messages = new[]
                {
                    new { role = "system", content = prompt.SystemPrompt },
                    new { role = "user", content = prompt.UserPrompt }
                },
                temperature = prompt.Temperature
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || AnsiSanitizer.DetectRateLimit(content))
            {
                return new AiCompletionResult(false, string.Empty, "Limite d'appels API atteinte (HTTP 429).", IsRateLimited: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AiCompletionResult(false, string.Empty, $"Erreur API HTTP {(int)response.StatusCode}: {content}");
            }

            using var doc = JsonDocument.Parse(content);
            var reply = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            return new AiCompletionResult(true, AnsiSanitizer.Sanitize(reply));
        }
        catch (OperationCanceledException)
        {
            return new AiCompletionResult(false, string.Empty, "Délai d'attente dépassé.");
        }
        catch (Exception ex)
        {
            return new AiCompletionResult(false, string.Empty, ex.Message);
        }
    }
}
