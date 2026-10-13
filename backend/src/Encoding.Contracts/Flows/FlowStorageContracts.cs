namespace Encoding.Contracts.Flows;

public sealed record SaveFlowRequest(int ExpectedVersion, FlowDefinition Definition);
public sealed record FlowVersionResponse(FlowDefinition Definition, string BackupStatus, DateTimeOffset CreatedAt);
public sealed record FlowSummary(Guid Id, string Name, string MediaType, int Version, int NodeCount, string BackupStatus, DateTimeOffset UpdatedAt);
