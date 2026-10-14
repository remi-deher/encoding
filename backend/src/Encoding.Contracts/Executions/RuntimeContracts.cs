using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Contracts.Executions;

public sealed record EnqueueMediaRequest(Guid Id, Guid LibraryId, string MediaPath, Guid FlowId, int FlowVersion);
public sealed record RuntimeLease(Guid ExecutionId, int Attempt, int Revision, long Epoch, DateTimeOffset ExpiresAt,
    Guid SourceNodeId, string? SourceRoot, string MediaPath, string? SourceSha256, FlowDefinition Flow, RuntimeCheckpoint? Checkpoint, string EntryNodeId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? PluginConfigurations = null);
public sealed record RuntimeFence(long Epoch);
public sealed record RuntimeLeaseStatus(bool CancelRequested, int Revision, DateTimeOffset ExpiresAt);
public sealed record RuntimeSourceState(string? SourceSha256, IReadOnlyDictionary<string,string>? Inputs = null);
public sealed record RuntimeTransfer(Guid ExecutionId, long Epoch, string Root, string MediaPath, string? SourceSha256,
    string? ResultSha256, string? ResultExtension, bool Publish, IReadOnlyList<string>? InputPaths = null, string MediaType = "video", bool CanWrite = true);
public sealed record RuntimeContext(string CurrentSha256, string Extension, IReadOnlyDictionary<string,JsonElement> Variables,
    JsonElement? BeforeMetadata, JsonElement? CurrentMetadata, IReadOnlyList<RuntimePlanStep> Plan);
public sealed record RuntimePlanStep(string ComponentId, IReadOnlyDictionary<string,JsonElement> Configuration);
public sealed record RuntimeCheckpoint(string NodeId, Guid InvocationId, string? OutputId, string? NextNodeId, RuntimeContext Context, string? TerminalResult);
public sealed record StartRuntimeStep(long Epoch, string NodeId);
public sealed record StartedRuntimeStep(Guid InvocationId, RuntimeCheckpoint? Completed);
public sealed record CompleteRuntimeStep(long Epoch, Guid EventId, int ExpectedRevision, RuntimeCheckpoint Checkpoint);
public sealed record FinishRuntimeExecution(long Epoch, Guid EventId, int ExpectedRevision, string State, string Detail, string? ResultSha256, string? Extension,
    JsonElement? BeforeMetadata = null, JsonElement? AfterMetadata = null, int? DeferMinutes = null);
public sealed record ConfirmRuntimePublication(long Epoch, Guid EventId, int ExpectedRevision, string PublishedPath, string Sha256);
