namespace Encoding.Contracts.Cluster;

public sealed record ClusterPeerPublicIdentity(Guid PeerId, string PublicKey);

/// <summary>An approver-signed membership grant. The data key is wrapped for the admitted peer only.</summary>
public sealed record ClusterPeerAdmissionGrant(
    Guid ClusterId,
    Guid PeerId,
    Guid? NodeId,
    string PublicKey,
    Guid ApprovedByPeerId,
    string? WrappedDataKey,
    string KeyFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset? RevokedAt,
    string Signature);

public sealed record ClusterJoinBootstrap(
    Guid ClusterId,
    string ClusterName,
    Guid PeerId,
    IReadOnlyList<ClusterPeerAdmissionGrant> Grants);
