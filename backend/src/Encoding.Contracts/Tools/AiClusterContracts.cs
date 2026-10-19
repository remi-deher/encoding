namespace Encoding.Contracts.Tools;

public sealed record AiNodeAvailability(Guid NodeId, string Name, IReadOnlyList<string> Providers, bool Reachable = true,
    IReadOnlyDictionary<string, string?>? Models = null);
public sealed record AiClusterView(IReadOnlyList<AiNodeAvailability> Nodes);
public sealed record AiSessionTurn(Guid RequestId, string UserPrompt, string Content, AiUsage? Usage = null, DateTimeOffset? CompletedAt = null);
public sealed record AiSessionState(string Id, string Provider, string SystemPrompt, IReadOnlyList<AiSessionTurn> Turns,
    Guid? PendingRequest = null, DateTimeOffset? AttemptDeadline = null, AiPrompt? PendingPrompt = null, int Attempt = 0,
    string? Model = null, DateTimeOffset? UpdatedAt = null, string? LastError = null, AiSessionScope? Scope = null,
    string? ReasoningEffort = null);
public sealed record AiSessionSummary(string Id, string Provider, string? Model, string Status,
    int TurnCount, int Attempt, DateTimeOffset? UpdatedAt, DateTimeOffset? AttemptDeadline, string? LastError,
    AiUsage? Usage, AiSessionScope? Scope = null);
public sealed record AiSessionPage(IReadOnlyList<AiSessionSummary> Items, int Offset, bool HasMore);
public sealed record AiGenerateRequest(string? Provider, AiPrompt Prompt);
public sealed record AiExecuteRequest(Guid Id, string Provider, AiPrompt Prompt, DateTimeOffset Deadline);
public sealed record ClaudeTokenRequest(string Token);
