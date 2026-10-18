namespace Encoding.Contracts.Tools;

public sealed record AiNodeAvailability(Guid NodeId, string Name, IReadOnlyList<string> Providers, bool Reachable = true);
public sealed record AiClusterView(IReadOnlyList<AiNodeAvailability> Nodes);
public sealed record AiSessionTurn(Guid RequestId, string UserPrompt, string Content);
public sealed record AiSessionState(string Id, string Provider, string SystemPrompt, IReadOnlyList<AiSessionTurn> Turns,
    Guid? PendingRequest = null, DateTimeOffset? AttemptDeadline = null, AiPrompt? PendingPrompt = null, int Attempt = 0);
public sealed record AiGenerateRequest(string? Provider, AiPrompt Prompt);
public sealed record AiExecuteRequest(Guid Id, string Provider, AiPrompt Prompt, DateTimeOffset Deadline);
public sealed record ClaudeTokenRequest(string Token);
