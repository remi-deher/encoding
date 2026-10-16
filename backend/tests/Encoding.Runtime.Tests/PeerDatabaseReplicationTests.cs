using System.Security.Cryptography;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.PeerCluster;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class PeerDatabaseReplicationTests
{
    [PostgresFact]
    public async Task TransactionsReplicateGeneratedValuesBinaryColumnsAccessesTasksAndSnapshots()
    {
        await using var source = await TestDatabase.CreateAsync(TestDatabase.ConfiguredConnection, "peer_source");
        await using var target = await TestDatabase.CreateAsync(TestDatabase.ConfiguredConnection, "peer_target");
        foreach (var db in new[] { source, target }) await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var capture = new PeerDatabaseSchema(source.Db);
        var projection = new PeerDatabaseSchema(target.Db);
        await capture.InitializeAsync(CancellationToken.None);
        await projection.InitializeAsync(CancellationToken.None);
        Assert.Equal(capture.Fingerprint, projection.Fingerprint);
        var node = Guid.NewGuid(); var library = Guid.NewGuid(); var execution = Guid.NewGuid();
        await using (var connection = await source.Db.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            async Task Run(string sql, params (string Key, object Value)[] values)
            {
                await using var command = Sql.Command(sql, connection, tx);
                foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
                await command.ExecuteNonQueryAsync();
            }
            await Run("UPDATE peer_replication_context SET active=1");
            await using (var insert = Sql.Command("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,node_token_hash) VALUES(@id,'storage',@roles,2,@caps,clock_timestamp(),@token)", connection, tx))
            {
                insert.Parameters.AddWithValue("id", node);
                insert.Parameters.AddWithValue("roles", SqlKind.TextArray, new[] { "worker", "server" });
                insert.Parameters.AddWithValue("caps", SqlKind.Json, """[{"toolId":"ffprobe"}]""");
                insert.Parameters.AddWithValue("token", SqlKind.Bytes, new byte[] { 0, 1, 127, 255 });
                await insert.ExecuteNonQueryAsync();
            }
            await Run("INSERT INTO libraries(id,name) VALUES(@id,'Shared library')", ("id", library));
            await Run("INSERT INTO library_node_accesses(library_id,node_id,mode,is_enabled,local_path,can_read,can_write) VALUES(@library,@node,'direct',true,'/mnt/media',true,true)", ("library", library), ("node", node));
            await using (var insert = Sql.Command("INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES(@id,'test.mkv','Shared library','flow',1,@definition,'queued',1)", connection, tx))
            {
                insert.Parameters.AddWithValue("id", execution);
                insert.Parameters.AddWithValue("definition", SqlKind.Json, """{"nodes":[]}""");
                await insert.ExecuteNonQueryAsync();
            }
            await Run("INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES(@execution,@library,'test.mkv','TEST.MKV')", ("execution", execution), ("library", library));
            await Run("INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,retry_of) SELECT @retry,media,library_name,flow_name,flow_version,flow_definition,'queued',2,id FROM executions WHERE id=@original", ("retry", Guid.NewGuid()), ("original", execution));
            var changes = await capture.ReadChangesAsync(connection, tx, CancellationToken.None);
            Assert.Equal(6, changes.Length);
            await Run("DELETE FROM peer_replication_changes; UPDATE peer_replication_context SET active=0");
            await tx.CommitAsync();
            await using var second = await target.Db.OpenConnectionAsync();
            await using var apply = await second.BeginTransactionAsync();
            await projection.ApplyChangesAsync(second, apply, changes, CancellationToken.None);
            await apply.CommitAsync();
        }
        Assert.Equal(await DatabaseTransfer.CountsAsync(source.Db, CancellationToken.None), await DatabaseTransfer.CountsAsync(target.Db, CancellationToken.None));
        await using (var command = target.Db.CreateCommand("SELECT node_token_hash,roles FROM cluster_nodes WHERE id=@id"))
        {
            command.Parameters.AddWithValue("id", node);
            await using var rows = await command.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(new byte[] { 0, 1, 127, 255 }, (byte[])rows[0]);
            Assert.Equal(new[] { "worker", "server" }, rows.GetStringArray(1));
        }
        await using (var connection = await source.Db.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            var image = await capture.CaptureAsync(connection, tx, 2, CancellationToken.None);
            await tx.RollbackAsync();
            await using var stale = target.Db.CreateCommand("INSERT INTO libraries(name) VALUES('Stale extra row')");
            await stale.ExecuteNonQueryAsync();
            await using var second = await target.Db.OpenConnectionAsync();
            await using var restore = await second.BeginTransactionAsync();
            await projection.RestoreAsync(second, restore, image, CancellationToken.None);
            await restore.CommitAsync();
        }
        Assert.Equal(await DatabaseTransfer.CountsAsync(source.Db, CancellationToken.None), await DatabaseTransfer.CountsAsync(target.Db, CancellationToken.None));
    }

    [PostgresFact]
    public async Task SharedWritesAreRolledBackWhenNoCommittedInitialStateExists()
    {
        await using var db = await TestDatabase.CreateAsync(TestDatabase.ConfiguredConnection, "peer_blocked");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var options = new PeerClusterOptions(true, Guid.NewGuid(), new Uri("https://localhost:8443/"), false, 8443,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), new string('x', 32), Path.GetTempPath(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var protection = new PeerSecretProtection(options, new EphemeralDataProtectionProvider());
        var coordinator = new PeerDatabaseCoordinator(new(db.Db), services, protection);
        await coordinator.InitializeAsync(CancellationToken.None);
        await using var wrapped = new PeerDataSource(coordinator);
        await using var command = wrapped.CreateCommand("INSERT INTO libraries(name) VALUES('Rejected')");
        await Assert.ThrowsAsync<PeerWriteUnavailableException>(() => command.ExecuteNonQueryAsync());
        await using var count = db.Db.CreateCommand("SELECT count(*) FROM libraries");
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public void SharedSecretsSurviveDifferentLocalKeyRingsAndRejectTampering()
    {
        var options = new PeerClusterOptions(true, Guid.NewGuid(), new Uri("https://localhost:8443/"), false, 8443,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), new string('x', 32), Path.GetTempPath(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var first = new PeerSecretProtection(options, new EphemeralDataProtectionProvider());
        var second = new PeerSecretProtection(options, new EphemeralDataProtectionProvider());
        var encrypted = first.Protect("private plugin setting"u8.ToArray());
        Assert.Equal("private plugin setting"u8.ToArray(), second.Unprotect(encrypted));
        encrypted[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => second.Unprotect(encrypted));
    }

    [PostgresFact]
    public async Task AnUncertainFormerLeaderRepairsItsProjectionWithoutAQuorumLease()
    {
        await using var db = await TestDatabase.CreateAsync(TestDatabase.ConfiguredConnection, "peer_repair");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await using (var insert = db.Db.CreateCommand("INSERT INTO libraries(name) VALUES('Committed state')"))
            await insert.ExecuteNonQueryAsync();
        var options = new PeerClusterOptions(true, Guid.NewGuid(), new Uri("https://localhost:8443/"), false, 8443,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), new string('x', 32), Path.GetTempPath(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        // Deliberately no consensus service: repairing a committed local image needs no leader lease.
        using var services = new ServiceCollection().BuildServiceProvider();
        var coordinator = new PeerDatabaseCoordinator(new(db.Db), services,
            new PeerSecretProtection(options, new EphemeralDataProtectionProvider()));
        await coordinator.InitializeAsync(CancellationToken.None);
        PeerDatabaseImage image;
        await using (var connection = await db.Db.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
            image = await coordinator.Schema.CaptureAsync(connection, tx, 1, CancellationToken.None);
        await coordinator.RestoreAsync(image, CancellationToken.None);
        await using (var drift = db.Db.CreateCommand("UPDATE libraries SET name='Uncertain SQL projection'"))
            await drift.ExecuteNonQueryAsync();
        coordinator.MarkUncertain();
        await coordinator.RepairAsync(CancellationToken.None);
        Assert.False(coordinator.NeedsRepair);
        await using var check = db.Db.CreateCommand("SELECT name FROM libraries");
        Assert.Equal("Committed state", await check.ExecuteScalarAsync());

        await using var local = await db.Db.OpenConnectionAsync();
        await using var noop = await local.BeginTransactionAsync();
        await using (var active = Sql.Command("UPDATE peer_replication_context SET active=1", local, noop))
            await active.ExecuteNonQueryAsync();
        await coordinator.PublishAsync(Guid.NewGuid(), local, noop, CancellationToken.None);
        await noop.CommitAsync();
        await using var context = db.Db.CreateCommand("SELECT active FROM peer_replication_context");
        Assert.Equal(0, Convert.ToInt32(await context.ExecuteScalarAsync()));
    }
}
