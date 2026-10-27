using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class RuntimeCommandJournalTests
{
    [Fact]
    public async Task LiveCommandUpdatesAreIdempotentAndCompletedAuditCannotBeReplacedByLateHeartbeat()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("command-journal");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), default);
        var id = Guid.NewGuid(); var invocation = Guid.NewGuid(); var commandId = Guid.NewGuid();
        await using (var insert = db.Db.CreateCommand("INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES(@id,'synthetic','test','test',1,'{}','running',1); INSERT INTO execution_steps(execution_id,node_id,invocation_id,idempotent) VALUES(@id,'check',@invocation,true)"))
        {
            insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("invocation", invocation); await insert.ExecuteNonQueryAsync();
        }
        var started = new RuntimeCommandLog(commandId, "ffmpeg", "", "", null, DateTimeOffset.UtcNow, null, false, 10);
        await using (var c = await db.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            Assert.True(await RuntimeCommandJournal.StoreAsync(id, [new("check", invocation, started)], c, tx, default));
            Assert.True(await RuntimeCommandJournal.StoreAsync(id, [new("check", invocation, started with { ProgressPercent = 50 })], c, tx, default));
            await tx.CommitAsync();
        }
        await using (var done = db.Db.CreateCommand("UPDATE execution_steps SET completed_at=clock_timestamp() WHERE execution_id=@id"))
        {
            done.Parameters.AddWithValue("id", id); await done.ExecuteNonQueryAsync();
        }
        await using (var c = await db.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            Assert.True(await RuntimeCommandJournal.StoreAsync(id, [new("check", invocation, started with { ProgressPercent = 99 })], c, tx, default));
            await tx.CommitAsync();
        }
        await using (var read = db.Db.CreateCommand("SELECT command_logs FROM execution_steps WHERE execution_id=@id"))
        {
            read.Parameters.AddWithValue("id", id);
            var logs = JsonSerializer.Deserialize<RuntimeCommandLog[]>((string)(await read.ExecuteScalarAsync())!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Single(logs); Assert.Equal(50, logs[0].ProgressPercent);
        }
    }
}
