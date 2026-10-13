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
    IReadOnlyList<string> AccelerationTypes);

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
