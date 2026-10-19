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
    DateTimeOffset UpdatedAt);

public sealed record AiAssistantTurn(
    Guid RequestId,
    string UserPrompt,
    string Content);

public sealed record AiAssistantConversationDetail(
    AiAssistantConversation Conversation,
    IReadOnlyList<AiAssistantTurn> Turns);

public sealed record AiAssistantMessageRequest(
    Guid RequestId,
    string UserPrompt,
    string? ConversationId = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    int? ResourceVersion = null);

public sealed record AiAssistantMessageResponse(
    bool Success,
    AiAssistantConversation Conversation,
    Guid RequestId,
    string? Content,
    string? ErrorMessage,
    IReadOnlyList<AiAssistantSource> Sources);
