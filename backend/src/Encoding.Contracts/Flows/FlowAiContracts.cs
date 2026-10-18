namespace Encoding.Contracts.Flows;

public sealed record FlowAiGenerateRequest(
    string MediaType,
    string UserPrompt,
    string? Provider = null,
    string? SessionId = null,
    Guid? RequestId = null);

public sealed record FlowAiRefactorRequest(
    FlowDefinition CurrentFlow,
    string UserPrompt,
    string? Provider = null,
    string? SessionId = null,
    Guid? RequestId = null);

public sealed record FlowAiExplainRequest(
    FlowDefinition Flow,
    string? Provider = null,
    string? SessionId = null,
    Guid? RequestId = null);

public sealed record FlowAiResponse(
    bool Success,
    FlowDefinition? Flow,
    string? Explanation = null,
    IReadOnlyList<FlowValidationIssue>? ValidationIssues = null,
    string? SessionId = null,
    Guid? RequestId = null);
