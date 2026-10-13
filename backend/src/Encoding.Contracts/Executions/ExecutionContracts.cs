using System.Text.Json;
namespace Encoding.Contracts.Executions;

public sealed record ExecutionResponse(
 Guid Id, string Media, string Library, string Flow, int FlowVersion,
 string State, Guid? NodeId, string? Node, decimal? Progress, string Detail,
 int Attempt, int Revision, DateTimeOffset QueuedAt, DateTimeOffset? StartedAt,
 DateTimeOffset? FinishedAt, DateTimeOffset UpdatedAt);
public sealed record ExecutionEventResponse(Guid Id, int Revision, string State, string Detail, DateTimeOffset CreatedAt);
public sealed record CreateExecutionRequest(Guid Id, string Media, string Library, string Flow, int FlowVersion, JsonElement FlowDefinition);
public sealed record ExecutionOverviewResponse(
 IReadOnlyList<ExecutionResponse> Executions, int QueuedCount, int ActiveCount,
 int FailedCount, int CancelledCount, int SucceededCount, DateTimeOffset ObservedAt,
 IReadOnlyList<ExecutionActivityResponse> Activity);
public sealed record ExecutionActivityResponse(Guid Id, Guid ExecutionId, string Media, string State, string Detail, DateTimeOffset CreatedAt);
public sealed record RecordExecutionProgress(Guid EventId, int ExpectedRevision, string State, Guid? NodeId, decimal? Progress, string Detail);
