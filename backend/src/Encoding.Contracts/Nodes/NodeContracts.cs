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
    IReadOnlyList<NodeToolCapability> Capabilities,
    NodeInventory? Inventory = null);

public sealed record NodeInventory(string OperatingSystem, string Architecture, int ProcessorCount, long MemoryMb);

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
    DateTimeOffset LastSeenAt,
    NodeInventory? Inventory = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string?>? Labels = null,
    IReadOnlyList<NodeRole>? DesiredRoles = null);

/// <summary>Demande d'un administrateur : mode de fonctionnement voulu pour un nœud, appliqué par le nœud à son prochain signal de présence (architecture 0036).</summary>
public sealed record ChangeNodeModeRequest(bool Server, bool Worker);

public sealed record NodeEnrollmentRequest(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    string NodeToken,
    NodeInventory? Inventory = null);

public sealed record CreateNodeEnrollmentRequest(NodeEnrollmentRequest Node, string PairingKey);

public sealed record ApproveNodeEnrollmentRequest(string PairingKey);

public sealed record PendingNodeEnrollmentResponse(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    NodeInventory? Inventory = null);

public sealed record NodeActivityResponse(int Active,int Succeeded,int Failed,int Cancelled,DateTimeOffset? LastActivity,int SourceTasks);

public sealed record NodeProcessingSettings(int? MaxConcurrentTasks,int EffectiveMaxConcurrentTasks,int Revision,NodeSchedule Schedule);
public sealed record SaveNodeProcessingSettings(int? MaxConcurrentTasks,int ExpectedRevision,NodeSchedule? Schedule=null);
public sealed record RuntimeCapacity(int MaxConcurrentTasks);
