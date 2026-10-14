namespace Encoding.Contracts.Flows;

public sealed record FlowTemplateSummary(
    Guid Id,
    string Name,
    string Description,
    string MediaType,
    Guid SourceFlowId,
    int SourceVersion,
    string SourceFlowName,
    int NodeCount,
    DateTimeOffset CreatedAt);

public sealed record FlowTemplateDetails(FlowTemplateSummary Template, FlowDefinition Definition);

public sealed record CreateFlowTemplateFromFlowRequest(
    Guid SourceFlowId,
    int SourceVersion,
    string Name,
    string Description);
