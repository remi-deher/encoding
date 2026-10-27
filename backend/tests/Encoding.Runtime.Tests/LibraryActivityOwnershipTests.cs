using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LibraryActivityOwnershipTests
{
    [Fact]
    public async Task NewerQuarantinedDuplicateCannotHideTheRetainedExecution()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("activity-ownership");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), default);
        var cluster = await new ClusterIdentityRepository(db.Db).GetAsync(default);
        var library = Guid.NewGuid(); var flow = Guid.NewGuid(); var scan = Guid.NewGuid(); var media = Guid.NewGuid();
        var retained = Guid.NewGuid(); var duplicate = Guid.NewGuid();
        await using (var insert = db.Db.CreateCommand("""
            INSERT INTO libraries(id,name) VALUES(@library,'Synthetic');
            INSERT INTO flows(id,current_version) VALUES(@flow,1);
            INSERT INTO flow_versions(flow_id,version,definition) VALUES(@flow,1,'{"name":"Synthetic"}');
            INSERT INTO library_flow_assignments(library_id,flow_id,flow_version,revision) VALUES(@library,@flow,1,1);
            INSERT INTO library_scans(id,library_id,media_type,state,completed_at) VALUES(@scan,@library,'video','completed',clock_timestamp());
            INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id) VALUES(@media,@library,'synthetic.mkv',1,clock_timestamp(),@scan);
            """))
        {
            insert.Parameters.AddWithValue("library",library); insert.Parameters.AddWithValue("flow",flow);
            insert.Parameters.AddWithValue("scan",scan); insert.Parameters.AddWithValue("media",media);
            await insert.ExecuteNonQueryAsync();
        }
        foreach (var (id,state) in new[] { (retained,"active"), (duplicate,"quarantined") })
        {
            await using var insert = db.Db.CreateCommand("""
                INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,updated_at) VALUES(@id,'synthetic.mkv','Synthetic','Synthetic',1,'{}','queued',1,@updated);
                INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES(@id,@library,'synthetic.mkv','SYNTHETIC.MKV');
                INSERT INTO task_ownership(execution_id,cluster_id,owner_peer_id,epoch,last_event_id,library_id,media_key,queued_at,state) VALUES(@id,@cluster,@peer,1,@event,@library,'SYNTHETIC.MKV',clock_timestamp(),@state);
                """);
            insert.Parameters.AddWithValue("id",id); insert.Parameters.AddWithValue("library",library);
            insert.Parameters.AddWithValue("cluster",cluster.Id); insert.Parameters.AddWithValue("peer",Guid.NewGuid()); insert.Parameters.AddWithValue("event",Guid.NewGuid());
            insert.Parameters.AddWithValue("state",state); insert.Parameters.AddWithValue("updated",DateTimeOffset.UtcNow.AddMinutes(state=="quarantined"?1:0));
            await insert.ExecuteNonQueryAsync();
        }
        var result = await new LibraryActivityRepository(db.Db).GetAsync(null,library,null,null,null,10,0,default);
        Assert.Equal(retained,Assert.Single(result.Files).ExecutionId);
    }
}
