using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Replication;
using System.Text.Json;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerConsensusStatus(bool IsLeader, string? LeaderEndpoint, IReadOnlyList<string> Members,
    long LastEntryIndex, long LastCommittedEntryIndex, bool HasQuorum);

public sealed class PeerConsensusService(IRaftHttpCluster cluster, PeerStateMachine stateMachine)
{
    private static readonly TimeSpan ConsensusOperationTimeout = TimeSpan.FromSeconds(8);

    public PeerConsensusStatus GetStatus()
    {
        var raftCluster = (IRaftCluster)cluster;
        var hasQuorum = raftCluster.TryGetLeaseToken(out var leaseToken) && !leaseToken.IsCancellationRequested;
        return new PeerConsensusStatus(cluster.Leader?.IsLeader == true, cluster.Leader?.EndPoint.ToString(),
            raftCluster.Members.Select(member => member.EndPoint.ToString() ?? string.Empty).Order(StringComparer.Ordinal).ToArray(),
            raftCluster.AuditTrail.LastEntryIndex, raftCluster.AuditTrail.LastCommittedEntryIndex, hasQuorum);
    }

    public bool TryGetLeaderLease(out CancellationToken leaseToken) =>
        ((IRaftCluster)cluster).TryGetLeaseToken(out leaseToken) && !leaseToken.IsCancellationRequested;

    public bool IsPeerRevoked(Guid peerId) => Get($"peer/revoked/{peerId:D}") is { ValueKind: JsonValueKind.True };

    public Task SetPeerRevokedAsync(Guid peerId, bool revoked, CancellationToken cancellationToken) =>
        ReplicateAsync(new PeerReplicatedStateCommand(Guid.NewGuid(), $"peer/revoked/{peerId:D}",
            JsonSerializer.SerializeToElement(revoked)), cancellationToken);

    public bool IsMember(Uri endpoint) => ((IRaftCluster)cluster).Members.Any(member =>
        string.Equals(Normalize(member.EndPoint.ToString()), Normalize(endpoint.ToString()), StringComparison.OrdinalIgnoreCase));

    public Task<bool> AddMemberAsync(Uri endpoint, CancellationToken cancellationToken) =>
        WithTimeoutAsync(token => cluster.AddMemberAsync(endpoint, token), cancellationToken);

    public Task<bool> RemoveMemberAsync(Uri endpoint, CancellationToken cancellationToken) =>
        WithTimeoutAsync(token => cluster.RemoveMemberAsync(endpoint, token), cancellationToken);

    public async Task ReplicateAsync(PeerReplicatedStateCommand command, CancellationToken cancellationToken)
    {
        var isClusterName = command.Key == "cluster/name" && command.Value.ValueKind == JsonValueKind.String
            && command.Value.GetString() is { Length: > 0 and <= 128 };
        var isPeerRevocation = command.Key.StartsWith("peer/revoked/", StringComparison.Ordinal)
            && Guid.TryParseExact(command.Key.AsSpan("peer/revoked/".Length), "D", out _)
            && command.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (!isClusterName && !isPeerRevocation)
            throw new ArgumentException("La commande de réplication ne correspond pas à une projection autorisée.");
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(command);
        if (bytes.Length > PeerStateMachine.MaximumCommandBytes) throw new ArgumentException("La commande dépasse la limite d’un mégaoctet.");
        await WithTimeoutAsync(async token =>
        {
            await ((IRaftCluster)cluster).ReplicateAsync((ReadOnlyMemory<byte>)bytes, null, token);
            return true;
        }, cancellationToken);
    }

    public JsonElement? Get(string key) => stateMachine.Get(key);

    private static string Normalize(string? endpoint) => (endpoint ?? string.Empty).TrimEnd('/');

    private static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(ConsensusOperationTimeout);
        try { return await operation(timeout.Token); }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new TimeoutException("L’opération Raft n’a pas été confirmée avant l’expiration du délai.", exception);
        }
    }
}
