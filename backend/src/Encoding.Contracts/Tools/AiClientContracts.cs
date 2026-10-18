namespace Encoding.Contracts.Tools;

/// <summary>Prompt passed to an AI text inference client.</summary>
public sealed record AiPrompt(
    string SystemPrompt,
    string UserPrompt,
    string? Model = null,
    double Temperature = 0.3,
    string? SessionId = null,
    Guid? RequestId = null);

/// <summary>Result returned by an AI text client.</summary>
public sealed record AiCompletionResult(
    bool Success,
    string Content,
    string? ErrorMessage = null,
    bool IsRateLimited = false,
    string? SessionId = null,
    Guid? RequestId = null);

/// <summary>Contract for AI text inference clients (CLI or HTTP API).</summary>
public interface IAiTextClient
{
    Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default);
}
