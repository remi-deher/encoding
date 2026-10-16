using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record TrustedPeer(Guid PeerId, Guid ClusterId, Uri Endpoint, string Thumbprint, string CertificatePem);
public sealed record TrustPeerRequest(Guid PeerId, Guid ClusterId, string Endpoint, string CertificatePem, string Fingerprint);

/// <summary>Explicit, durable pin set. Discovery never adds trust by itself.</summary>
public sealed class PeerTrustStore
{
    private readonly PeerClusterOptions options;
    private readonly PeerIdentity localIdentity;
    private readonly string filePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ImmutableDictionary<Guid, TrustedPeer> peers;
    private PeerStateMachine? replicatedState;

    public PeerTrustStore(PeerClusterOptions options, PeerIdentity localIdentity, PeerStateMachine? replicatedState = null)
    {
        this.options = options;
        this.localIdentity = localIdentity;
        this.replicatedState = replicatedState;
        filePath = Path.Combine(options.DataDirectory, "trusted-peers.json");
        peers = Load();
    }

    public IReadOnlyList<TrustedPeer> List() => peers.Values.Where(peer => !IsRevoked(peer.PeerId)).OrderBy(peer => peer.PeerId).ToArray();

    public TrustedPeer? Find(Guid peerId) => IsRevoked(peerId) ? null : peers.GetValueOrDefault(peerId);

    public void AttachStateMachine(PeerStateMachine stateMachine) => Interlocked.Exchange(ref replicatedState, stateMachine);

    public bool IsRevoked(Guid peerId) => Volatile.Read(ref replicatedState)?.Get($"peer/revoked/{peerId:D}") is { ValueKind: JsonValueKind.True };

    public TrustedPeer? FindByCertificate(X509Certificate2 certificate)
    {
        var thumbprint = PeerIdentity.Fingerprint(certificate);
        return peers.Values.FirstOrDefault(peer => !IsRevoked(peer.PeerId) && peer.Thumbprint == thumbprint
            && PeerIdentityStore.CertificateSubjectMatches(certificate, peer.ClusterId, peer.PeerId)
            && IsCurrentlyValid(certificate));
    }

    public async Task<TrustedPeer> TrustAsync(TrustPeerRequest request, CancellationToken cancellationToken)
    {
        if (request.PeerId == localIdentity.PeerId) throw new ArgumentException("Un pair ne peut pas s’ajouter lui-même.");
        if (request.ClusterId != localIdentity.ClusterId) throw new ArgumentException("Le pair proposé appartient à un autre cluster.");
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("L’adresse du pair doit être une origine HTTPS sans chemin, identifiants ni paramètres.");

        using var certificate = X509Certificate2.CreateFromPem(request.CertificatePem);
        var actualFingerprint = PeerIdentity.Fingerprint(certificate);
        var expectedFingerprint = PeerIdentity.NormalizeThumbprint(request.Fingerprint);
        if (actualFingerprint.Length != 64 || expectedFingerprint != actualFingerprint)
            throw new ArgumentException("L’empreinte fournie ne correspond pas au certificat.");
        if (!PeerIdentityStore.CertificateSubjectMatches(certificate, request.ClusterId, request.PeerId))
            throw new ArgumentException("Le certificat ne porte pas l’identité du pair annoncée.");
        if (!IsCurrentlyValid(certificate) || certificate.HasPrivateKey)
            throw new ArgumentException("Le certificat public du pair est invalide ou contient une clé privée.");

        var peer = new TrustedPeer(request.PeerId, request.ClusterId, endpoint, actualFingerprint, certificate.ExportCertificatePem());
        await gate.WaitAsync(cancellationToken);
        try
        {
            var next = peers.SetItem(peer.PeerId, peer);
            await SaveAsync(next, cancellationToken);
            peers = next;
        }
        finally { gate.Release(); }
        return peer;
    }

    public async Task<bool> RevokeAsync(Guid peerId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!peers.ContainsKey(peerId)) return false;
            var next = peers.Remove(peerId);
            await SaveAsync(next, cancellationToken);
            peers = next;
            return true;
        }
        finally { gate.Release(); }
    }

    public bool IsTrustedClientCertificate(X509Certificate2? certificate) => certificate is not null && FindByCertificate(certificate) is not null;

    public X509Certificate2? LoadCertificate(TrustedPeer peer) => X509Certificate2.CreateFromPem(peer.CertificatePem);

    private ImmutableDictionary<Guid, TrustedPeer> Load()
    {
        if (!File.Exists(filePath)) return ImmutableDictionary<Guid, TrustedPeer>.Empty;
        var loaded = JsonSerializer.Deserialize<TrustedPeer[]>(File.ReadAllText(filePath)) ?? [];
        var result = ImmutableDictionary.CreateBuilder<Guid, TrustedPeer>();
        foreach (var peer in loaded)
        {
            if (peer.ClusterId != localIdentity.ClusterId || peer.PeerId == localIdentity.PeerId
                || !Uri.TryCreate(peer.Endpoint.ToString(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
                || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/")
                throw new InvalidOperationException("Le fichier des pairs approuvés contient une entrée invalide.");
            using var certificate = X509Certificate2.CreateFromPem(peer.CertificatePem);
            if (PeerIdentity.Fingerprint(certificate) != peer.Thumbprint
                || !PeerIdentityStore.CertificateSubjectMatches(certificate, peer.ClusterId, peer.PeerId))
                throw new InvalidOperationException("L’empreinte d’un pair approuvé ne correspond pas à son certificat.");
            result.Add(peer.PeerId, peer);
        }
        return result.ToImmutable();
    }

    private async Task SaveAsync(ImmutableDictionary<Guid, TrustedPeer> value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var temporary = filePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value.Values.OrderBy(peer => peer.PeerId)), cancellationToken);
        PeerIdentityStore.SetPrivateFilePermissions(temporary);
        File.Move(temporary, filePath, overwrite: true);
        PeerIdentityStore.SetPrivateFilePermissions(filePath);
    }

    private static bool IsCurrentlyValid(X509Certificate2 certificate)
    {
        var now = DateTimeOffset.UtcNow;
        return certificate.NotBefore.ToUniversalTime() <= now && certificate.NotAfter.ToUniversalTime() > now;
    }
}
