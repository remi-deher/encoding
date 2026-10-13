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
