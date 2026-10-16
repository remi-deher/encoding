namespace Encoding.Api.Infrastructure.PeerCluster;

/// <summary>
/// Encodes the Raft term and the per-work-item attempt in the fencing value sent to workers.
/// A former leader's token becomes stale as soon as the cluster elects a new term.
/// </summary>
public static class PeerLeaseEpoch
{
    private const int SequenceBits = 31;
    private const ulong SequenceMask = (1UL << SequenceBits) - 1;

    public static long Next(long term, long previousEpoch)
    {
        if (term is < 1 or > uint.MaxValue)
            throw new InvalidOperationException("Le terme Raft ne peut pas être encodé dans un bail worker.");
        if (previousEpoch < 0)
            throw new InvalidOperationException("L’époque de bail précédente est invalide.");

        var sequence = ((ulong)previousEpoch & SequenceMask) + 1;
        if (sequence > SequenceMask)
            throw new InvalidOperationException("Le compteur de bail de cette tâche a atteint sa limite.");

        return checked((long)(((ulong)term << SequenceBits) | sequence));
    }

    public static bool IsForTerm(long epoch, long term) =>
        epoch > 0 && term > 0 && ((ulong)epoch >> SequenceBits) == (ulong)term;
}
