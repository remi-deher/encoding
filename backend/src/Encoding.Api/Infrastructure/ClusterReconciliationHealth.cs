namespace Encoding.Api.Infrastructure;

public sealed record ClusterReconciliationPeerHealth(
    string Url,
    bool Configured,
    bool Discovered,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError);

public sealed record ClusterReconciliationHealthStatus(
    bool Enabled,
    string? DisabledReason,
    bool DiscoveryAvailable,
    string? DiscoveryError,
    IReadOnlyList<ClusterReconciliationPeerHealth> Peers);

/// <summary>Publie l'état opérationnel du maillage sans exposer les clés privées du cluster.</summary>
public sealed class ClusterReconciliationHealth
{
    private readonly object gate = new();
    private readonly Dictionary<string, MutablePeerHealth> peers = new(StringComparer.OrdinalIgnoreCase);
    private bool enabled;
    private string? disabledReason;
    private bool discoveryAvailable;
    private string? discoveryError;

    public void Disable(string reason)
    {
        lock (gate)
        {
            enabled = false;
            disabledReason = reason;
        }
    }

    public void Enable()
    {
        lock (gate)
        {
            enabled = true;
            disabledReason = null;
        }
    }

    public void SetDiscovery(bool available, string? error = null)
    {
        lock (gate)
        {
            discoveryAvailable = available;
            discoveryError = error;
        }
    }

    public void SetPeers(IEnumerable<string> configured, IEnumerable<string> discovered)
    {
        lock (gate)
        {
            foreach (var peer in peers.Values)
            {
                peer.Configured = false;
                peer.Discovered = false;
            }

            foreach (var url in configured)
                GetOrAdd(url).Configured = true;
            foreach (var url in discovered)
                GetOrAdd(url).Discovered = true;

            foreach (var stale in peers.Where(entry => !entry.Value.Configured && !entry.Value.Discovered
                         && entry.Value.LastAttemptAt is null).Select(entry => entry.Key).ToArray())
                peers.Remove(stale);
        }
    }

    public void BeginAttempt(string url)
    {
        lock (gate) GetOrAdd(url).LastAttemptAt = DateTimeOffset.UtcNow;
    }

    public void Succeed(string url)
    {
        lock (gate)
        {
            var peer = GetOrAdd(url);
            peer.LastAttemptAt = DateTimeOffset.UtcNow;
            peer.LastSuccessAt = peer.LastAttemptAt;
            peer.LastError = null;
        }
    }

    public void Fail(string url, string error)
    {
        lock (gate)
        {
            var peer = GetOrAdd(url);
            peer.LastAttemptAt = DateTimeOffset.UtcNow;
            peer.LastError = error;
        }
    }

    public ClusterReconciliationHealthStatus GetStatus()
    {
        lock (gate)
            return new(enabled, disabledReason, discoveryAvailable, discoveryError,
                peers.OrderBy(peer => peer.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new ClusterReconciliationPeerHealth(entry.Key, entry.Value.Configured,
                        entry.Value.Discovered, entry.Value.LastAttemptAt, entry.Value.LastSuccessAt, entry.Value.LastError))
                    .ToArray());
    }

    private MutablePeerHealth GetOrAdd(string url)
    {
        if (!peers.TryGetValue(url, out var peer)) peers[url] = peer = new MutablePeerHealth();
        return peer;
    }

    private sealed class MutablePeerHealth
    {
        public bool Configured { get; set; }
        public bool Discovered { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public string? LastError { get; set; }
    }
}
