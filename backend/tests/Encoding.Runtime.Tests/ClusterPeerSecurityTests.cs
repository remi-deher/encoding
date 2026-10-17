using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterPeerSecurityTests
{
    [Fact]
    public async Task ApprovedPeerReceivesClusterKeyAndSignedTrustChain()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-peer-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var source = await TestDatabase.CreateSqliteAsync("peer-security-source");
            await using var joining = await TestDatabase.CreateSqliteAsync("peer-security-joining");
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(source.Db, migrations, CancellationToken.None);
            await DatabaseMigrator.ApplyAsync(joining.Db, migrations, CancellationToken.None);

            var clusterId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            await new ClusterIdentityRepository(source.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
            await new ClusterIdentityRepository(joining.Db).AdoptAsync(Guid.NewGuid(), "Autonome", CancellationToken.None);

            using var sourceIdentity = CreateIdentity(Path.Combine(root, "source-keys"));
            using var joiningIdentity = CreateIdentity(Path.Combine(root, "joining-keys"));
            var sharedKey = RandomNumberGenerator.GetBytes(32);
            using var sourceDataKey = new ClusterDataKeyStore(clusterId, sharedKey);
            var joiningDataKey = new ClusterDataKeyStore(Configuration(Path.Combine(root, "joining-keys")), new EphemeralDataProtectionProvider());
            var sourceAdmissions = new ClusterPeerAdmissionRepository(source.Db, new ClusterIdentityRepository(source.Db), sourceIdentity, sourceDataKey);
            var joiningAdmissions = new ClusterPeerAdmissionRepository(joining.Db, new ClusterIdentityRepository(joining.Db), joiningIdentity, joiningDataKey);

            await sourceAdmissions.EnsureRootAdmissionAsync(clusterId, CancellationToken.None);
            await sourceAdmissions.CreateDirectApprovalGrantAsync(nodeId, joiningIdentity.PeerId, joiningIdentity.PublicKey, CancellationToken.None);
            var bootstrap = await sourceAdmissions.GetBootstrapForNodeAsync(nodeId, CancellationToken.None);
            Assert.NotNull(bootstrap);
            Assert.DoesNotContain(Convert.ToBase64String(sharedKey), JsonSerializer.Serialize(bootstrap));

            var ownGrant = Assert.Single(bootstrap.Grants, grant => grant.PeerId == joiningIdentity.PeerId);
            var invalidGrant = ownGrant with { Signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(384)) };
            var forged = bootstrap with { Grants = bootstrap.Grants.Select(grant => grant.PeerId == ownGrant.PeerId ? invalidGrant : grant).ToArray() };
            await Assert.ThrowsAsync<ArgumentException>(() => joiningAdmissions.ImportBootstrapAsync(forged, nodeId, CancellationToken.None));

            await joiningAdmissions.ImportBootstrapAsync(bootstrap, nodeId, CancellationToken.None);
            await new ClusterIdentityRepository(joining.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);

            Assert.Equal(sharedKey, joiningDataKey.GetRequired(clusterId));
            Assert.Equal(sourceIdentity.PublicKey,
                await joiningAdmissions.GetActivePublicKeyAsync(clusterId, sourceIdentity.PeerId, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ClusterPeerIdentityStore CreateIdentity(string keyDirectory) =>
        new(Configuration(keyDirectory), new EphemeralDataProtectionProvider());

    private static IConfiguration Configuration(string keyDirectory) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["PluginSettings:KeyDirectory"] = keyDirectory })
        .Build();
}
