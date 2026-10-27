using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
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
            INSERT INTO execution_steps(execution_id,node_id,invocation_id,idempotent,command_logs)
            VALUES(@id,'unavailable-block',@invocation,true,'null')
            """))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("state", state);
            insert.Parameters.AddWithValue("invocation", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
        }
        var step = Assert.Single(await new ExecutionRepository(db.Db).PipelineAsync(id, CancellationToken.None));
        Assert.Equal("unavailable-block", step.NodeId);
        Assert.Empty(step.Commands);
        Assert.Equal(state, step.Status);
    }
}
