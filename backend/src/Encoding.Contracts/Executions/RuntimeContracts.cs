using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Contracts.Executions;

public sealed record EnqueueMediaRequest(Guid Id, Guid LibraryId, string MediaPath, Guid FlowId, int FlowVersion);
public sealed record RuntimeLease(Guid ExecutionId, int Attempt, int Revision, long Epoch, DateTimeOffset ExpiresAt,
    Guid SourceNodeId, string? SourceRoot, string MediaPath, string? SourceSha256, FlowDefinition Flow, RuntimeCheckpoint? Checkpoint, string EntryNodeId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? PluginConfigurations = null, Guid? LibraryId = null);
public sealed record RuntimeFence(long Epoch);
public sealed record RuntimeLeaseStatus(bool CancelRequested, int Revision, DateTimeOffset ExpiresAt);
public sealed record RuntimeSourceState(string? SourceSha256, IReadOnlyDictionary<string,string>? Inputs = null);
public sealed record RuntimeTransfer(Guid ExecutionId, long Epoch, string Root, string MediaPath, string? SourceSha256,
    string? ResultSha256, string? ResultExtension, bool Publish, IReadOnlyList<string>? InputPaths = null, string MediaType = "video", bool CanWrite = true, int? ReplacementGraceHours = null);
public sealed record RuntimeContext(string CurrentSha256, string Extension, IReadOnlyDictionary<string,JsonElement> Variables,
    JsonElement? BeforeMetadata, JsonElement? CurrentMetadata, IReadOnlyList<RuntimePlanStep> Plan,
    IReadOnlyList<ResolvedVideoEncoding>? VideoEncodings = null);
public sealed record ResolvedVideoEncoding(string FlowNodeId, Guid WorkerId, string Codec, string EncoderId,
    string FfmpegVersion, string Acceleration, string QualityMode, string QualityOption, decimal Quality,
    string PresetId, string? PresetOption, string? PresetValue,
    Guid ProfileId, int ProfileVersion, string ProfileName, int Priority, DateTimeOffset ResolvedAt);
public sealed record RuntimePlanStep(string ComponentId, IReadOnlyDictionary<string,JsonElement> Configuration, string? FlowNodeId = null);
public sealed record RuntimeCheckpoint(string NodeId, Guid InvocationId, string? OutputId, string? NextNodeId, RuntimeContext Context, string? TerminalResult,
    RuntimeHandoffSelector? Handoff = null);
public sealed record RuntimeCommandLog(Guid Id, string Command, string StandardOutput, string StandardError, int? ExitCode,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, bool Truncated);
public sealed record RuntimeStepCommand(string NodeId, Guid InvocationId, RuntimeCommandLog Command);
public sealed record ExecutionPipelineStepResponse(string NodeId, string NodeName, Guid InvocationId,
    string ComponentKind, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string? OutputId, string? OutputLabel, string? NextNodeId, IReadOnlyList<RuntimeCommandLog> Commands);
public sealed record StartRuntimeStep(long Epoch, string NodeId);
public sealed record StartedRuntimeStep(Guid InvocationId, RuntimeCheckpoint? Completed);
public sealed record CompleteRuntimeStep(long Epoch, Guid EventId, int ExpectedRevision, RuntimeCheckpoint Checkpoint,
    IReadOnlyList<RuntimeCommandLog>? Commands = null);
public sealed record FinishRuntimeExecution(long Epoch, Guid EventId, int ExpectedRevision, string State, string Detail, string? ResultSha256, string? Extension,
    JsonElement? BeforeMetadata = null, JsonElement? AfterMetadata = null, int? DeferMinutes = null,
    IReadOnlyList<RuntimeStepCommand>? Commands = null);
public sealed record ReportRuntimeCommand(long Epoch, string NodeId, Guid InvocationId, RuntimeCommandLog Command);
public sealed record ConfirmRuntimePublication(long Epoch, Guid EventId, int ExpectedRevision, string PublishedPath, string Sha256, SourceReplacementReport? Replacement = null);
