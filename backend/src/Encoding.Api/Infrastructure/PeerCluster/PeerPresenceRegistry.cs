using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerPresenceUnsigned(Guid ClusterId, Guid PeerId, long Incarnation, long Sequence,
    DateTimeOffset PublishedAtUtc, IReadOnlyList<NodeResponse> Nodes);
public sealed record PeerPresenceRecord(Guid ClusterId, Guid PeerId, long Incarnation, long Sequence,
    DateTimeOffset PublishedAtUtc, IReadOnlyList<NodeResponse> Nodes, string Signature);
public sealed record PeerPresenceBatch(IReadOnlyList<PeerPresenceRecord> Records);
public sealed record PeerView(Guid PeerId, string Endpoint, string Thumbprint, string Status,
    DateTimeOffset? LastSeenAt, int NodeCount, int OnlineNodeCount);
public sealed record PeerClusterView(Guid ClusterId, Guid LocalPeerId, string LocalThumbprint,
    string LocalCertificatePem, IReadOnlyList<PeerView> Peers, IReadOnlyList<NodeResponse> Nodes,
    bool PresenceMayDiverge);

/// <summary>In-memory, eventually consistent peer observations. Messages keep their origin signature when relayed.</summary>
public sealed class PeerPresenceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, PeerPresenceRecord> records = new();

    public static PeerPresenceRecord Sign(PeerPresenceUnsigned message, PeerIdentityStore identity)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        return new PeerPresenceRecord(message.ClusterId, message.PeerId, message.Incarnation, message.Sequence,
            message.PublishedAtUtc, message.Nodes, Convert.ToBase64String(identity.Sign(payload)));
    }

    public bool Merge(PeerPresenceRecord incoming, Guid expectedClusterId, TrustedPeer trustedPeer)
    {
        if (incoming.ClusterId != expectedClusterId || incoming.PeerId != trustedPeer.PeerId
            || incoming.ClusterId != trustedPeer.ClusterId || incoming.Incarnation < 1 || incoming.Sequence < 1
            || incoming.PublishedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5)
            || !string.Equals(PeerIdentity.NormalizeThumbprint(trustedPeer.Thumbprint), ReadThumbprint(trustedPeer), StringComparison.Ordinal))
            return false;
        byte[] signature;
        try { signature = Convert.FromBase64String(incoming.Signature); }
        catch (FormatException) { return false; }
        using var certificate = X509Certificate2.CreateFromPem(trustedPeer.CertificatePem);
        using var publicKey = certificate.GetECDsaPublicKey();
        if (publicKey is null) return false;
        var unsigned = new PeerPresenceUnsigned(incoming.ClusterId, incoming.PeerId, incoming.Incarnation,
            incoming.Sequence, incoming.PublishedAtUtc, incoming.Nodes);
        if (!publicKey.VerifyData(JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions), signature, HashAlgorithmName.SHA256)) return false;

        while (true)
        {
            if (!records.TryGetValue(incoming.PeerId, out var existing))
            {
                if (records.TryAdd(incoming.PeerId, incoming)) return true;
                continue;
            }
            if (incoming.Incarnation < existing.Incarnation
                || incoming.Incarnation == existing.Incarnation && incoming.Sequence <= existing.Sequence) return false;
            if (records.TryUpdate(incoming.PeerId, incoming, existing)) return true;
        }
    }

    public IReadOnlyList<PeerPresenceRecord> Snapshot() => records.Values.OrderBy(record => record.PeerId).ToArray();

    public bool Remove(Guid peerId) => records.TryRemove(peerId, out _);

    public IReadOnlyList<NodeResponse> MergeNodes(IReadOnlyList<NodeResponse> localNodes, TimeSpan offlineAfter)
    {
        var latest = new Dictionary<Guid, NodeResponse>();
        foreach (var node in localNodes) latest[node.Id] = WithCurrentStatus(node, offlineAfter);
        foreach (var node in records.Values.SelectMany(record => record.Nodes))
        {
            var normalized = WithCurrentStatus(node, offlineAfter);
            if (!latest.TryGetValue(node.Id, out var current) || normalized.LastSeenAt > current.LastSeenAt)
                latest[node.Id] = normalized;
        }
        return latest.Values.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Id).ToArray();
    }

    public IReadOnlyList<PeerView> BuildPeerViews(PeerTrustStore trustStore, TimeSpan offlineAfter)
    {
        var snapshot = records.ToDictionary(pair => pair.Key, pair => pair.Value);
        var now = DateTimeOffset.UtcNow;
        return trustStore.List().Select(peer =>
        {
            snapshot.TryGetValue(peer.PeerId, out var presence);
            var age = presence is null ? (TimeSpan?)null : now - presence.PublishedAtUtc;
            var state = age is null ? "unknown" : age <= offlineAfter / 2 ? "online" : age <= offlineAfter ? "suspect" : "offline";
            var nodes = presence?.Nodes ?? [];
            return new PeerView(peer.PeerId, peer.Endpoint.ToString(), peer.Thumbprint, state,
                presence?.PublishedAtUtc, nodes.Count, nodes.Count(node => WithCurrentStatus(node, offlineAfter).Status == NodePresenceStatus.Online));
        }).OrderBy(peer => peer.PeerId).ToArray();
    }

    private static NodeResponse WithCurrentStatus(NodeResponse node, TimeSpan offlineAfter)
    {
        var online = node.LastSeenAt >= DateTimeOffset.UtcNow - offlineAfter;
        var lifecycle = node.Lifecycle switch
        {
            NodeLifecycle.Online or NodeLifecycle.Offline => online ? NodeLifecycle.Online : NodeLifecycle.Offline,
            _ => node.Lifecycle
        };
        return node with
        {
            Status = online ? NodePresenceStatus.Online : NodePresenceStatus.Offline,
            Lifecycle = lifecycle
        };
    }

    private static string ReadThumbprint(TrustedPeer peer)
    {
        using var certificate = X509Certificate2.CreateFromPem(peer.CertificatePem);
        return PeerIdentity.Fingerprint(certificate);
    }
}
