using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using System.Text.Json;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ExecutionPipelineHistoryTests
{
    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("running")]
    public async Task PartialReplicatedHistoryRemainsReadable(string state)
    {
        await using var db = await TestDatabase.CreateAsync(RuntimeFixture.ConnectionString, "pipeline_history");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var id = Guid.NewGuid();
        await using (var insert = db.Db.CreateCommand("""
            INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt)
            VALUES(@id,'synthetic.mkv','Synthetic','Partial',1,'{}',@state,1);
            INSERT INTO execution_steps(execution_id,node_id,invocation_id,idempotent,command_logs,checkpoint)
            VALUES(@id,'unavailable-block',@invocation,true,'null','{}')
            """))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("state", state);
            insert.Parameters.AddWithValue("invocation", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
        }
        Assert.NotNull(await new ExecutionRepository(db.Db).GetAsync(id, default));
        var step = Assert.Single(await new ExecutionRepository(db.Db).PipelineAsync(id, CancellationToken.None));
        Assert.Equal("unavailable-block", step.NodeId);
        Assert.Empty(step.Commands);
        Assert.Equal(state, step.Status);
    }

    [Fact]
    public async Task DuplicateNodeIdsInHistoricalFlowDoNotBreakPipelineDiagnostics()
    {
        await using var db = await TestDatabase.CreateAsync(RuntimeFixture.ConnectionString, "pipeline_duplicate_nodes");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var (registry, _, _) = Harness.Services();
        var valid = Harness.Chain(
            Harness.Node(registry, "entry", "base-input-file"),
            Harness.Node(registry, "finish", "base-complete-flow"));
        var partiallyReconciled = valid with { Nodes = [.. valid.Nodes, valid.Nodes[1]] };
        var serialized = JsonSerializer.Serialize(partiallyReconciled, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var id = Guid.NewGuid();
        await using (var insert = db.Db.CreateCommand("""
            INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt)
            VALUES(@id,'synthetic.mkv','Synthetic','Duplicate node fixture',1,@flow,'failed',1);
            INSERT INTO execution_steps(execution_id,node_id,invocation_id,idempotent,command_logs,checkpoint)
            VALUES(@id,'finish',@invocation,true,'[]','{}')
            """))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("flow", serialized);
            insert.Parameters.AddWithValue("invocation", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
        }

        var step = Assert.Single(await new ExecutionRepository(db.Db).PipelineAsync(id, CancellationToken.None));
        Assert.Equal("finish", step.NodeId);
        Assert.Equal("finish", step.NodeName);
        Assert.Equal("failed", step.Status);
    }
}
