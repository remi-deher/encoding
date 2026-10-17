using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Architecture 0043 : l'export/import entre SQLite et PostgreSQL conserve les données dans les deux sens.</summary>
public sealed class DatabaseTransferTests
{
    private static async Task<TestDatabase> MigratedAsync(string kind, string? postgres)
    {
        var database = string.Equals(kind, "sqlite", StringComparison.OrdinalIgnoreCase)
            ? await TestDatabase.CreateSqliteAsync("transfer")
            : await TestDatabase.CreatePostgresAsync(postgres, "transfer");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        return database;
    }

    private static async Task SeedAsync(TestDatabase database, Guid node, Guid library, Guid execution)
    {
        async Task Run(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = database.Db.CreateCommand(sql);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
        await using (var insert = database.Db.CreateCommand("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,node_token_hash) VALUES(@id,'alpha',@roles,3,@caps,clock_timestamp(),@token)"))
        {
            insert.Parameters.AddWithValue("id", node);
            insert.Parameters.AddWithValue("roles", SqlKind.TextArray, new[] { "server", "worker" });
            insert.Parameters.AddWithValue("caps", SqlKind.Json, """[{"toolId":"ffmpeg"}]""");
            insert.Parameters.AddWithValue("token", SqlKind.Bytes, new byte[] { 1, 2, 3 });
            await insert.ExecuteNonQueryAsync();
        }
        await Run("INSERT INTO libraries(id,name) VALUES(@id,'films')", ("id", library));
        await Run("INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES(@id,'a.mkv','films','flow',1,@definition,'queued',1)",
            ("id", execution), ("definition", "{}"));
        await Run("INSERT INTO execution_work(execution_id,library_id,media_path,media_key,available_at) VALUES(@id,@library,'a.mkv','A.MKV','infinity')", ("id", execution), ("library", library));
    }

    private static async Task AssertSeedAsync(TestDatabase database, Guid node, Guid library, Guid execution)
    {
        await using (var roles = database.Db.CreateCommand("SELECT roles, max_concurrent_tasks, name FROM cluster_nodes WHERE id=@id"))
        {
            roles.Parameters.AddWithValue("id", node);
            await using var reader = await roles.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(["server", "worker"], reader.GetStringArray(0));
            Assert.Equal(3, reader.GetInt32(1));
            Assert.Equal("alpha", reader.GetString(2));
        }
        await using (var infinite = database.Db.CreateCommand("SELECT EXISTS(SELECT 1 FROM execution_work WHERE execution_id=@id AND library_id=@library AND available_at='infinity')"))
        {
            infinite.Parameters.AddWithValue("id", execution); infinite.Parameters.AddWithValue("library", library);
            Assert.True(await infinite.ScalarBoolAsync());
        }
    }

    private static async Task SeedDefinitionAsync(TestDatabase database, Guid execution)
    {
        await using var command = database.Db.CreateCommand("UPDATE executions SET flow_definition=@definition WHERE id=@id");
        command.Parameters.AddWithValue("id", execution);
        command.Parameters.AddWithValue("definition", SqlKind.Json, """{"nodes":[]}""");
        await command.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task DataSurvivesSqliteToPostgresAndBack()
    {
        var postgres = TestDatabase.ConfiguredConnection;
        if (TestDatabase.IsSqlite) return;
        var node = Guid.NewGuid(); var library = Guid.NewGuid(); var execution = Guid.NewGuid();
        await using var first = await MigratedAsync("sqlite", null);
        await SeedAsync(first, node, library, execution);
        await SeedDefinitionAsync(first, execution);

        await using var middle = await MigratedAsync("postgres", postgres);
        var toPostgres = await DatabaseTransfer.CopyAsync(first.Db, middle.Db, CancellationToken.None);
        Assert.Equal(1, toPostgres.Rows["cluster_nodes"]);
        Assert.Equal(await DatabaseTransfer.CountsAsync(first.Db, CancellationToken.None), await DatabaseTransfer.CountsAsync(middle.Db, CancellationToken.None));
        await AssertSeedAsync(middle, node, library, execution);

        await using var last = await MigratedAsync("sqlite", null);
        await DatabaseTransfer.CopyAsync(middle.Db, last.Db, CancellationToken.None);
        Assert.Equal(await DatabaseTransfer.CountsAsync(first.Db, CancellationToken.None), await DatabaseTransfer.CountsAsync(last.Db, CancellationToken.None));
        await AssertSeedAsync(last, node, library, execution);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTransfer.CopyAsync(first.Db, middle.Db, CancellationToken.None));
    }

    [Fact]
    public async Task SqliteToSqliteCopyKeepsEveryTable()
    {
        var node = Guid.NewGuid(); var library = Guid.NewGuid(); var execution = Guid.NewGuid();
        await using var source = await MigratedAsync("sqlite", null);
        await SeedAsync(source, node, library, execution);
        await SeedDefinitionAsync(source, execution);
        await using var target = await MigratedAsync("sqlite", null);
        var result = await DatabaseTransfer.CopyAsync(source.Db, target.Db, CancellationToken.None);
        Assert.True(result.Total >= 4);
        Assert.Equal(await DatabaseTransfer.CountsAsync(source.Db, CancellationToken.None), await DatabaseTransfer.CountsAsync(target.Db, CancellationToken.None));
        await AssertSeedAsync(target, node, library, execution);
    }
}
