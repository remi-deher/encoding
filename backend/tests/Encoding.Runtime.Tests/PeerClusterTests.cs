using Encoding.Api.Infrastructure.PeerCluster;
using Encoding.Contracts.Nodes;
using System.Security.Cryptography;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class PeerClusterTests
{
    [Fact]
    public void IdentitySurvivesRestartAndIncrementsIncarnation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-peer-identity-" + Guid.NewGuid().ToString("N"));
        var clusterId = Guid.NewGuid();
        try
        {
            Guid peerId;
            string thumbprint;
            long incarnation;
            using (var first = PeerIdentityStore.Open(directory, clusterId))
            {
                peerId = first.Identity.PeerId;
                thumbprint = first.Identity.Thumbprint;
                Assert.Equal(64, thumbprint.Length);
                incarnation = first.StartNewIncarnation();
                Assert.True(incarnation > 0);
                Assert.Throws<InvalidOperationException>(() => PeerIdentityStore.Open(directory, clusterId));
            }

            using var restarted = PeerIdentityStore.Open(directory, clusterId);
            Assert.Equal(peerId, restarted.Identity.PeerId);
            Assert.Equal(thumbprint, restarted.Identity.Thumbprint);
            Assert.True(restarted.StartNewIncarnation() > incarnation);
            Assert.Throws<InvalidOperationException>(() => PeerIdentityStore.Open(directory, Guid.NewGuid()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void IdentityPfxIsEncryptedAndRequiresItsNodeLocalPassword()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-peer-pfx-" + Guid.NewGuid().ToString("N"));
        var clusterId = Guid.NewGuid();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            Guid peerId;
            using (var identity = PeerIdentityStore.Open(directory, clusterId, pfxPassword: password))
                peerId = identity.Identity.PeerId;

            Assert.Throws<CryptographicException>(() => PeerIdentityStore.Open(directory, clusterId, pfxPassword: "wrong password"));
            using var restored = PeerIdentityStore.Open(directory, clusterId, pfxPassword: password);
            Assert.Equal(peerId, restored.Identity.PeerId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TrustRequiresThePinnedIdentityAndCanBeRevoked()
    {
        var directoryA = Path.Combine(Path.GetTempPath(), "encoding-peer-a-" + Guid.NewGuid().ToString("N"));
        var directoryB = Path.Combine(Path.GetTempPath(), "encoding-peer-b-" + Guid.NewGuid().ToString("N"));
        var clusterId = Guid.NewGuid();
        try
        {
            using var a = PeerIdentityStore.Open(directoryA, clusterId);
            using var b = PeerIdentityStore.Open(directoryB, clusterId);
            var options = Options(directoryA, clusterId, "https://peer-a:8443/");
            var replicatedState = new PeerStateMachine(options);
            var trust = new PeerTrustStore(options, a.Identity, replicatedState);
            var request = new TrustPeerRequest(b.Identity.PeerId, clusterId, "https://peer-b:8443/",
                b.Identity.CertificatePem, b.Identity.Thumbprint);

            await Assert.ThrowsAsync<ArgumentException>(() => trust.TrustAsync(request with { Fingerprint = new string('0', 64) }, CancellationToken.None));
            var trusted = await trust.TrustAsync(request, CancellationToken.None);
            Assert.Equal(b.Identity.Thumbprint, trusted.Thumbprint);
            Assert.NotNull(trust.FindByCertificate(b.Identity.Certificate));
            Assert.True(trust.IsTrustedClientCertificate(b.Identity.Certificate));
            Assert.True(await trust.RevokeAsync(b.Identity.PeerId, CancellationToken.None));
            Assert.False(trust.IsTrustedClientCertificate(b.Identity.Certificate));
            Assert.False(await trust.RevokeAsync(b.Identity.PeerId, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directoryA)) Directory.Delete(directoryA, recursive: true);
            if (Directory.Exists(directoryB)) Directory.Delete(directoryB, recursive: true);
        }
    }

    [Fact]
    public async Task PresenceSignatureConvergesAndRejectsTamperingAndReplay()
    {
        var directoryA = Path.Combine(Path.GetTempPath(), "encoding-presence-a-" + Guid.NewGuid().ToString("N"));
        var directoryB = Path.Combine(Path.GetTempPath(), "encoding-presence-b-" + Guid.NewGuid().ToString("N"));
        var clusterId = Guid.NewGuid();
        try
        {
            using var a = PeerIdentityStore.Open(directoryA, clusterId);
            using var b = PeerIdentityStore.Open(directoryB, clusterId);
            var options = Options(directoryA, clusterId, "https://peer-a:8443/");
            var trust = new PeerTrustStore(options, a.Identity, new PeerStateMachine(options));
            var trusted = await trust.TrustAsync(new TrustPeerRequest(b.Identity.PeerId, clusterId, "https://peer-b:8443/",
                b.Identity.CertificatePem, b.Identity.Thumbprint), CancellationToken.None);
            var node = new NodeResponse(Guid.NewGuid(), "worker-1", [NodeRole.Worker], 2, [], NodePresenceStatus.Online,
                DateTimeOffset.UtcNow, new NodeInventory("linux", "x64", 4096, 8));
            var signed = PeerPresenceRegistry.Sign(new PeerPresenceUnsigned(clusterId, b.Identity.PeerId, 1, 1,
                DateTimeOffset.UtcNow, [node]), b);
            var registry = new PeerPresenceRegistry();

            Assert.True(registry.Merge(signed, clusterId, trusted));
            Assert.False(registry.Merge(signed, clusterId, trusted));
            var tampered = signed with { Nodes = [] };
            Assert.False(registry.Merge(tampered, clusterId, trusted));
            Assert.Single(registry.MergeNodes([], TimeSpan.FromSeconds(30)));
            Assert.Empty(registry.MergeNodes([], TimeSpan.FromSeconds(30), replicatedCatalog: true));
            var catalogNode = node with { Name = "Committed name", LastSeenAt = node.LastSeenAt.AddSeconds(-5) };
            var merged = Assert.Single(registry.MergeNodes([catalogNode], TimeSpan.FromSeconds(30), replicatedCatalog: true));
            Assert.Equal(catalogNode.Name, merged.Name);
            Assert.Equal(node.LastSeenAt, merged.LastSeenAt);

            var newer = PeerPresenceRegistry.Sign(new PeerPresenceUnsigned(clusterId, b.Identity.PeerId, 1, 2,
                DateTimeOffset.UtcNow, [node with { Name = "worker-1-renamed" }]), b);
            Assert.True(registry.Merge(newer, clusterId, trusted));
            Assert.Equal("worker-1-renamed", Assert.Single(registry.MergeNodes([], TimeSpan.FromSeconds(30))).Name);
            Assert.False(registry.Merge(signed, clusterId, trusted));
        }
        finally
        {
            if (Directory.Exists(directoryA)) Directory.Delete(directoryA, recursive: true);
            if (Directory.Exists(directoryB)) Directory.Delete(directoryB, recursive: true);
        }
    }

    private static PeerClusterOptions Options(string directory, Guid clusterId, string endpoint) =>
        new(true, clusterId, new Uri(endpoint), false, 8443, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), "test-password-that-is-over-32-characters", directory);
}
