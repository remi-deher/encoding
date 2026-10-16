using System.Collections.Concurrent;
namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerObservation(Guid PeerId, long Revision, DateTimeOffset ObservedAtUtc);
public sealed class PeerObservations
{
    private readonly ConcurrentDictionary<Guid, PeerObservation> observations = new();
    public void Expect(Guid peerId) => observations.TryAdd(peerId, new(peerId, 0, DateTimeOffset.UtcNow));
    public void Observe(PeerProtocolStatus status) => observations[status.PeerId] = new(status.PeerId, status.Revision, DateTimeOffset.UtcNow);
    public IReadOnlyList<PeerObservation> List() => observations.Values.ToArray();
}
