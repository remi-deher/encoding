namespace Encoding.Contracts.Nodes;

public enum NodeRole
{
    Server,
    Worker
}

public sealed record NodeHeartbeatRequest(
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities);

public sealed record NodeToolCapability(
    string ToolId,
    string Version,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<string> AccelerationTypes,
    IReadOnlyList<string>? Encoders = null,
    IReadOnlyList<NodeEncoderCapability>? EncoderChecks = null);

/// <summary>Result of a safe synthetic encode check for a catalogued video encoder.</summary>
public sealed record NodeEncoderCapability(string EncoderId, string Status);

public enum NodePresenceStatus
{
    Online,
    Offline
}

public sealed record NodeResponse(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    NodePresenceStatus Status,
    DateTimeOffset LastSeenAt);

public sealed record NodeEnrollmentRequest(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    string NodeToken);

public sealed record CreateNodeEnrollmentRequest(NodeEnrollmentRequest Node, string PairingKey);

public sealed record ApproveNodeEnrollmentRequest(string PairingKey);

public sealed record PendingNodeEnrollmentResponse(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record NodeActivityResponse(int Active,int Succeeded,int Failed,int Cancelled,DateTimeOffset? LastActivity,int SourceTasks);

public sealed record NodeProcessingSettings(int? MaxConcurrentTasks,int EffectiveMaxConcurrentTasks,int Revision);
public sealed record SaveNodeProcessingSettings(int? MaxConcurrentTasks,int ExpectedRevision);
public sealed record RuntimeCapacity(int MaxConcurrentTasks);
