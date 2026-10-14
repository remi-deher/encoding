namespace Encoding.Contracts.Libraries;

public sealed record CreateLibraryRequest(string Name);

public sealed record LibraryResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    int ConfiguredNodeCount = 0,
    int DirectAccessNodeCount = 0,
    int TransferNodeCount = 0,
    bool IsActive = true, string? FlowName = null, int? FlowVersion = null);

public sealed record UpdateLibraryRequest(string Name);

public sealed record SetLibraryActivationRequest(bool IsActive);

public enum LibraryNodeAccessMode
{
    Direct,
    Unsupported,
    Transfer
}

public sealed record LibraryNodeAccessRequest(
    Guid NodeId,
    LibraryNodeAccessMode Mode,
    string? LocalPath,
    bool CanRead,
    bool CanWrite,
    Guid? TransferSourceNodeId,
    bool IsEnabled = true);

public sealed record ConfigureLibraryNodeAccessRequest(IReadOnlyList<LibraryNodeAccessRequest> Nodes);

public sealed record SetLibraryNodeAccessActivationRequest(bool IsEnabled);

public sealed record LibraryNodeAccessResponse(
    Guid LibraryId,
    Guid NodeId,
    string NodeName,
    LibraryNodeAccessMode Mode,
    bool IsEnabled,
    string? LocalPath,
    bool CanRead,
    bool CanWrite,
    Guid? TransferSourceNodeId,
    string? TransferSourceNodeName,
    DateTimeOffset UpdatedAt);
