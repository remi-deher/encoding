using DotNext.Net.Cluster.Consensus.Raft.Http;

namespace Encoding.Api.Infrastructure.PeerCluster;

public interface IPeerAssignmentGate
{
    CancellationTokenSource? AcquireLease(CancellationToken requestToken);
    long CurrentTerm { get; }
    long NextFencingEpoch(long previousEpoch);
    bool IsCurrentFencingEpoch(long epoch);
}

/// <summary>New jobs are assigned only by a peer holding a current quorum-backed Raft leader lease.</summary>
public sealed class PeerAssignmentGate(IRaftHttpCluster cluster) : IPeerAssignmentGate
{
    private DotNext.Net.Cluster.Consensus.Raft.IRaftCluster RaftCluster => (DotNext.Net.Cluster.Consensus.Raft.IRaftCluster)cluster;

    public long CurrentTerm => RaftCluster.AuditTrail.Term;

    public long NextFencingEpoch(long previousEpoch) => PeerLeaseEpoch.Next(CurrentTerm, previousEpoch);

    public bool IsCurrentFencingEpoch(long epoch) => PeerLeaseEpoch.IsForTerm(epoch, CurrentTerm);

    public CancellationTokenSource? AcquireLease(CancellationToken requestToken)
    {
        if (!RaftCluster.TryGetLeaseToken(out var leaseToken)
            || leaseToken.IsCancellationRequested) return null;
        return CancellationTokenSource.CreateLinkedTokenSource(requestToken, leaseToken);
    }
}

public sealed class PeerClusterQuorumHealthCheck(PeerConsensusService consensus) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = consensus.GetStatus();
        var data = new Dictionary<string, object>
        {
            ["isLeader"] = status.IsLeader,
            ["hasQuorum"] = status.HasQuorum,
            ["memberCount"] = status.Members.Count,
            ["committedIndex"] = status.LastCommittedEntryIndex
        };
        var result = status.HasQuorum
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Le pair possède un bail Raft valide.", data: data)
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded("Aucun quorum Raft confirmé ; les nouvelles affectations sont suspendues.", data: data);
        return Task.FromResult(result);
    }
}
