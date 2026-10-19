namespace Encoding.Contracts.Tools;

public sealed record AiAssistantSource(
    string Kind,
    Guid ResourceId,
    string Label,
    string Href,
    int? Version = null);

public sealed record AiAssistantConversation(
    string Id,
    string Provider,
    string Title,
    AiAssistantSource Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AiAssistantSource>? AdditionalSources = null);

public sealed record AiAssistantResourceReference(string Type, Guid Id, int? Version = null);

public sealed record AiAssistantSourcesRequest(IReadOnlyList<AiAssistantResourceReference> Sources);

public sealed record AiAssistantToolCall(string Name, Guid? FlowId = null, string? State = null, int? Limit = null);

public sealed record AiAssistantToolResult(string Name, string Label, string Href, JsonElement Data);

public sealed record AiAssistantTurn(
    Guid RequestId,
    string UserPrompt,
    string Content,
    IReadOnlyList<AiAssistantSource>? Sources = null);

public sealed record AiAssistantConversationDetail(
    AiAssistantConversation Conversation,
    IReadOnlyList<AiAssistantTurn> Turns,
    AiAssistantPendingTurn? PendingTurn = null);

public sealed record AiAssistantPendingTurn(Guid RequestId, string UserPrompt, bool IsRunning);

public sealed record AiAssistantMessageRequest(
    Guid RequestId,
    string UserPrompt,
    string? ConversationId = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    int? ResourceVersion = null,
    IReadOnlyList<AiAssistantToolCall>? ToolCalls = null);

public sealed record AiAssistantMessageResponse(
    bool Success,
    AiAssistantConversation Conversation,
    Guid RequestId,
    string? Content,
    string? ErrorMessage,
    IReadOnlyList<AiAssistantSource> Sources);

public sealed record AiAssistantActivity(
    Guid RequestId,
    string? ConversationId,
    string Provider,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long? DurationMilliseconds,
    string? ErrorCategory,
    string Operation = "assistant",
    IReadOnlyList<string>? Tools = null);
