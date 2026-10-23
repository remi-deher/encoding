using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Xunit;
using Xunit.Abstractions;

namespace Encoding.Runtime.Tests;

public sealed class RuntimeQueryPlanTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("worker_node_id", "ix_execution_work_worker_lease")]
    [InlineData("library_id", "ix_execution_work_library_lease")]
    public async Task LoadQueriesUseACoveringIndexInsteadOfScanningAllWork(string column, string index)
    {
        await using var database = await CreateAsync();
        var sql = $"SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.{column}=@id AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')";
        var after = await PlanAsync(database, sql);
        Assert.Contains(after, line => line.Contains("COVERING INDEX " + index, StringComparison.Ordinal));
        Assert.DoesNotContain(after, line => line.Contains("SCAN w", StringComparison.Ordinal));
        await using (var drop = database.Db.CreateCommand("DROP INDEX " + index)) await drop.ExecuteNonQueryAsync();
        var before = await PlanAsync(database, sql);
        Assert.Contains(before, line => line.Contains("SCAN w", StringComparison.Ordinal));
        output.WriteLine("Before: " + string.Join("; ", before));
        output.WriteLine("After: " + string.Join("; ", after));
    }

    [Fact]
    public async Task LatestCheckpointLookupDoesNotSortAllSteps()
    {
        await using var database = await CreateAsync();
        const string sql = "SELECT checkpoint FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1";
        var after = await PlanAsync(database, sql);
        Assert.Contains(after, line => line.Contains("ix_execution_steps_latest_checkpoint", StringComparison.Ordinal));
        Assert.DoesNotContain(after, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        await using (var drop = database.Db.CreateCommand("DROP INDEX ix_execution_steps_latest_checkpoint")) await drop.ExecuteNonQueryAsync();
        var before = await PlanAsync(database, sql);
        Assert.Contains(before, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        output.WriteLine("Before: " + string.Join("; ", before));
        output.WriteLine("After: " + string.Join("; ", after));
    }

    private static async Task<TestDatabase> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("query-plans");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        return database;
    }

    private static async Task<List<string>> PlanAsync(TestDatabase database, string sql)
    {
        await using var command = database.Db.CreateCommand("EXPLAIN QUERY PLAN " + sql);
        command.Parameters.AddWithValue("id", Guid.Empty);
        await using var rows = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await rows.ReadAsync()) plan.Add(rows.GetString(3));
        return plan;
    }
}
