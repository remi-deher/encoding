using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.PeerCluster;
using Xunit;

namespace Encoding.Runtime.Tests;

[Collection("peer-operations")]
public sealed class PeerOperationsTests
{
    [DatabaseFact]
    public async Task AuthorityInitializationCopiesIntoEmptyDatabaseAndRefusesAMerge()
    {
        await using var source = await TestDatabase.CreateAsync(TestDatabase.ConfiguredConnection, "authority_source");
        await using var target = await TestDatabase.CreateAsync(TestDatabase.ConfiguredConnection, "authority_target");
        await DatabaseMigrator.ApplyAsync(source.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await using (var insert = source.Db.CreateCommand("INSERT INTO libraries(name) VALUES('Authoritative library')")) await insert.ExecuteNonQueryAsync();
        string[] args = ["--authority", source.ConnectionString, "--target", target.ConnectionString];
        Assert.Equal(0, await PeerAuthorityDatabaseCommand.RunAsync(args, Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
        var expected = await DatabaseTransfer.CountsAsync(source.Db, CancellationToken.None);
        Assert.Equal(expected, await DatabaseTransfer.CountsAsync(target.Db, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PeerAuthorityDatabaseCommand.RunAsync(args, Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
        Assert.Equal(expected, await DatabaseTransfer.CountsAsync(source.Db, CancellationToken.None));
    }
    [DatabaseFact]
    public async Task ColdBackupRestoresSameIdentityDatabaseAndKeysAndRejectsTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-peer-backup-" + Guid.NewGuid().ToString("N"));
        var peer = Path.Combine(root, "peer"); var keys = Path.Combine(root, "keys"); var backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(keys);
        await File.WriteAllTextAsync(Path.Combine(keys, "test-key.xml"), "synthetic local key");
        var cluster = Guid.NewGuid(); var set = Guid.NewGuid();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var oldPassword = Environment.GetEnvironmentVariable("PeerCluster__IdentityPassword");
        Environment.SetEnvironmentVariable("PeerCluster__IdentityPassword", password);
        try
        {
            await using var source = await TestDatabase.CreateAsync(TestDatabase.ConfiguredConnection, "backup_source");
            await using var target = await TestDatabase.CreateAsync(TestDatabase.ConfiguredConnection, "backup_target");
            await DatabaseMigrator.ApplyAsync(source.Db, Harness.RepositoryRoot(), CancellationToken.None);
            await using (var insert = source.Db.CreateCommand("INSERT INTO libraries(name) VALUES('From authority')")) await insert.ExecuteNonQueryAsync();
            Guid identityId;
            string[] backupArguments = ["--data-directory", peer, "--cluster-id", cluster.ToString(),
                "--database", source.ConnectionString, "--key-directory", keys, "--output", backup, "--backup-set", set.ToString()];
            using (var identity = PeerIdentityStore.Open(peer, cluster, pfxPassword: password))
            {
                identityId = identity.Identity.PeerId;
                await Assert.ThrowsAsync<InvalidOperationException>(() => PeerOperationsCommand.RunAsync(
                    "backup-peer", backupArguments, Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
            }
            await Assert.ThrowsAsync<ArgumentException>(() => PeerOperationsCommand.RunAsync("backup-peer",
                backupArguments.Select(value => value == backup ? Path.Combine(keys, "recursive-archive") : value).ToArray(),
                Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
            Assert.Equal(0, await PeerOperationsCommand.RunAsync("backup-peer", backupArguments, Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
            var manifest = JsonSerializer.Deserialize<PeerBackupManifest>(await File.ReadAllTextAsync(Path.Combine(backup, "manifest.json")))!;
            Assert.Equal(identityId, manifest.PeerId);
            var restoredPeer = Path.Combine(root, "restored-peer"); var restoredKeys = Path.Combine(root, "restored-keys");
            string[] restoreArguments = ["--data-directory", restoredPeer, "--cluster-id", cluster.ToString(),
                "--database", target.ConnectionString, "--key-directory", restoredKeys, "--input", backup, "--backup-set", set.ToString()];
            Assert.Equal(0, await PeerOperationsCommand.RunAsync("restore-peer", restoreArguments, Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
            Assert.Equal(await DatabaseTransfer.CountsAsync(source.Db, CancellationToken.None), await DatabaseTransfer.CountsAsync(target.Db, CancellationToken.None));
            using (var restored = PeerIdentityStore.Open(restoredPeer, cluster, pfxPassword: password))
                Assert.Equal(identityId, restored.Identity.PeerId);
            Assert.Equal("synthetic local key", await File.ReadAllTextAsync(Path.Combine(restoredKeys, "test-key.xml")));
            await File.AppendAllTextAsync(Path.Combine(backup, "keys", "test-key.xml"), "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => PeerOperationsCommand.RunAsync(
                "restore-peer", restoreArguments, Harness.RepositoryRoot(), TextWriter.Null, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PeerCluster__IdentityPassword", oldPassword);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CertificateRotationKeepsPeerIdentityAndRequiresTheApprovedFingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-cert-rotate-" + Guid.NewGuid().ToString("N"));
        var cluster = Guid.NewGuid();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            Guid peerId; string fingerprint;
            using (var identity = PeerIdentityStore.Open(directory, cluster, pfxPassword: password))
            {
                peerId = identity.Identity.PeerId;
                using var next = identity.PrepareRotation("localhost", password).Certificate;
                fingerprint = PeerIdentity.Fingerprint(next);
                Assert.NotEqual(identity.Identity.Thumbprint, fingerprint);
                Assert.Throws<InvalidOperationException>(() => identity.ActivateRotation(password, new string('A', 64)));
                identity.ActivateRotation(password, fingerprint);
            }
            using var restarted = PeerIdentityStore.Open(directory, cluster, pfxPassword: password);
            Assert.Equal(peerId, restarted.Identity.PeerId);
            Assert.Equal(fingerprint, restarted.Identity.Thumbprint);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12345)]
    public void FencingIncludesTermZeroAndRejectsEarlierTerms(long term)
    {
        var epoch = PeerLeaseEpoch.Next(term, 0);
        Assert.True(PeerLeaseEpoch.IsForTerm(epoch, term));
        Assert.False(PeerLeaseEpoch.IsForTerm(epoch, term + 1));
        Assert.True(PeerLeaseEpoch.Next(term, epoch) > epoch);
    }
}
