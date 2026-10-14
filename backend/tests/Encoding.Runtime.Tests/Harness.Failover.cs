using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Api.Executions;
using Encoding.Api.Libraries;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        private async Task<T?> ScalarAsync<T>(string sql,params (string Name,object Value)[] parameters)
        {
            await using var command=db.CreateCommand(sql);
            foreach(var (name,value) in parameters) command.Parameters.AddWithValue(name,value);
            var result=await command.ExecuteScalarAsync();
            return result is null or DBNull?default:(T)result;
        }
        private async Task ResetNodesAsync()
        {
            await using var command=db.CreateCommand("UPDATE cluster_nodes SET last_seen_at=clock_timestamp(),max_concurrent_tasks=10");
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Exécution terminée côté worker : résultat enregistré, publication demandée au nœud d'accès (a).</summary>
        private async Task<(Guid Id,RuntimeLease Lease,Guid Library,string SourceSha,string ResultSha)> PublicationRequestedAsync(bool replacement,int graceHours=1)
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var(id,lease,context)=await EnqueueAsync(flow); var library=lease.LibraryId!.Value;
            if(replacement) Check(await new SourceReplacementRepository(db).SavePolicyAsync(library,new(true,graceHours,0),CancellationToken.None),"Enable the replacement policy.");
            await CompleteAsync(id,a,lease,"entry",context); await CompleteAsync(id,a,lease,"end",context);
            var result=System.Text.Encoding.UTF8.GetBytes("encoded-result-"+id); var resultSha=Convert.ToHexStringLower(SHA256.HashData(result));
            Check(await artifacts.PutAsync(id,a,lease.Epoch,resultSha,new MemoryStream(result),false,CancellationToken.None),"Register the validated result.");
            var status=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew before finishing.");
            Check(await new RuntimeTransferRepository(db).FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"succeeded","Result validated",resultSha,".mkv",J(new{v="before"}),J(new{v="after"})),CancellationToken.None),"The worker requests publication.");
            return(id,lease,library,context.CurrentSha256,resultSha);
        }
        private static SourceReplacementReport Replaced(Guid id,long bytes=21)=>new("replaced","source.mkv",SourceReplacer.BackupPath(id,"source.mkv"),bytes,new DateTimeOffset(2024,5,6,7,8,9,TimeSpan.Zero),null);

        public async Task ReplacementLifecycleAsync()
        {
            await ResetNodesAsync();
            var policies=new SourceReplacementRepository(db); var transfers=new RuntimeTransferRepository(db);
            var fresh=Guid.NewGuid();
            await using(var create=db.CreateCommand("INSERT INTO libraries(id,name) VALUES(@id,'policy')")){ create.Parameters.AddWithValue("id",fresh); await create.ExecuteNonQueryAsync(); }
            Check(await policies.GetPolicyAsync(fresh,CancellationToken.None) is { Enabled: false, Revision: 0 },"Source replacement is disabled by default.");
            Check(await policies.GetPolicyAsync(Guid.NewGuid(),CancellationToken.None) is null,"An unknown library has no policy.");
            Check(await policies.SavePolicyAsync(fresh,new(true,24,0),CancellationToken.None) && !await policies.SavePolicyAsync(fresh,new(false,24,0),CancellationToken.None),"A stale policy revision is rejected.");
            var invalid=false; try{ await policies.SavePolicyAsync(fresh,new(true,0,1),CancellationToken.None); }catch(ArgumentException){ invalid=true; }
            Check(invalid && (await policies.GetPolicyAsync(fresh,CancellationToken.None))!.GracePeriodHours==24,"An invalid grace period is rejected without changing the policy.");

            // Policy disabled: the source is never replaced, even if an agent reports a replacement.
            var disabled=await PublicationRequestedAsync(false);
            Check(await ScalarAsync<int?>("SELECT replacement_grace_hours FROM execution_work WHERE execution_id=@id",("id",disabled.Id)) is null,"No replacement is requested while the policy is disabled.");
            var disabledPath=$".encoding-results/{disabled.Id:D}/result.mkv";
            var status=await executions.GetAsync(disabled.Id,CancellationToken.None);
            Check(await transfers.ConfirmAsync(disabled.Id,a,new(disabled.Lease.Epoch,Guid.NewGuid(),status!.Revision,disabledPath,disabled.ResultSha,Replaced(disabled.Id)),CancellationToken.None),"Publication is confirmed.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM source_replacements WHERE execution_id=@id",("id",disabled.Id))==0,"A report received while the policy is disabled is ignored.");

            // Enabled: the request carries the policy snapshot; forged reports are refused.
            var item=await PublicationRequestedAsync(true,2);
            var publication=(await transfers.ListAsync(a,CancellationToken.None)).Single(transfer=>transfer.ExecutionId==item.Id && transfer.Publish);
            Check(publication.ReplacementGraceHours==2,"The transfer request carries the library policy snapshot.");
            Check(await policies.SavePolicyAsync(item.Library,new(false,2,1),CancellationToken.None) && (await transfers.ListAsync(a,CancellationToken.None)).Single(transfer=>transfer.ExecutionId==item.Id).ReplacementGraceHours==2,"Changing the policy later does not alter a request already issued.");
            var path=$".encoding-results/{item.Id:D}/result.mkv"; status=await executions.GetAsync(item.Id,CancellationToken.None);
            Check(!await transfers.ConfirmAsync(item.Id,a,new(item.Lease.Epoch,Guid.NewGuid(),status!.Revision,path,item.ResultSha,Replaced(item.Id) with { BackupPath="elsewhere/original.mkv" }),CancellationToken.None),"A backup outside .encoding-replaced is refused.");
            Check(!await transfers.ConfirmAsync(item.Id,a,new(item.Lease.Epoch,Guid.NewGuid(),status.Revision,path,item.ResultSha,Replaced(item.Id) with { FinalPath="other/source.mkv" }),CancellationToken.None),"A replacement outside the media directory is refused.");
            Check(await ScalarAsync<string>("SELECT state FROM executions WHERE id=@id",("id",item.Id))=="running","Refused reports leave the execution unfinished.");
            var confirm=new ConfirmRuntimePublication(item.Lease.Epoch,Guid.NewGuid(),status.Revision,path,item.ResultSha,Replaced(item.Id));
            Check(await transfers.ConfirmAsync(item.Id,a,confirm,CancellationToken.None),"A valid report completes the execution.");
            Check(!await transfers.ConfirmAsync(item.Id,a,confirm with { EventId=Guid.NewGuid() },CancellationToken.None) && await ScalarAsync<long>("SELECT count(*) FROM source_replacements WHERE execution_id=@id",("id",item.Id))==1,"A retried confirmation through another request is rejected and never records a second replacement.");
            var record=await policies.GetAsync(item.Id,CancellationToken.None);
            Check(record is { State: "replaced" } && record.GraceUntil>DateTimeOffset.UtcNow.AddHours(1.9) && record.SourceSha256==item.SourceSha && record.ResultSha256==item.ResultSha,"The replacement is recorded with its grace deadline and digests.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_replacement_marks WHERE execution_id=@id AND relative_path='source.mkv' AND byte_length=21",("id",item.Id))==1,"The produced file is marked so monitoring does not re-queue it.");
            Check((await ScalarAsync<string>("SELECT detail FROM executions WHERE id=@id",("id",item.Id)))!.Contains("source remplacée"),"The execution history states that the source was replaced.");

            // Automatic monitoring must not process the freshly replaced file again.
            await AutomaticMonitoringSkipsReplacedFileAsync(item.Library,item.Id);

            // Rollback is only possible inside the grace period, and reserves the media.
            Check(await policies.RequestRollbackAsync(Guid.NewGuid(),CancellationToken.None)=="missing","Unknown replacements cannot be restored.");
            Check(await policies.RequestRollbackAsync(item.Id,CancellationToken.None)=="ok" && await policies.RequestRollbackAsync(item.Id,CancellationToken.None)=="not-restorable","A rollback is requested once.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id AND lease_epoch=-1",("id",item.Id))==1,"The media is reserved during the rollback.");
            var task=(await policies.TasksAsync(a,CancellationToken.None)).Single(task=>task.ExecutionId==item.Id);
            Check(task.Action=="rollback" && task.Root==directory && task.BackupPath==Replaced(item.Id).BackupPath,"The access node receives the rollback with the library root.");
            Check((await policies.TasksAsync(b,CancellationToken.None)).Count==0 && !await policies.ReportAsync(item.Id,b,new("rolled_back",null,21,DateTimeOffset.UtcNow),CancellationToken.None),"Only the node that holds the access can report the result.");
            Check(await policies.ReportAsync(item.Id,a,new("failed","Fichier modifié"),CancellationToken.None) && (await policies.GetAsync(item.Id,CancellationToken.None))!.State=="replaced"
                && (await policies.GetAsync(item.Id,CancellationToken.None))!.Detail=="Fichier modifié","A refused rollback returns to the grace period and keeps the reason.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id AND lease_epoch=-1",("id",item.Id))==0,"The rollback reservation is released after a failure.");
            Check(await policies.RequestRollbackAsync(item.Id,CancellationToken.None)=="ok","The rollback can be retried.");
            Check(!await policies.ReportAsync(item.Id,a,new("finalized",null),CancellationToken.None),"A purge cannot be reported for a rollback.");
            Check(await policies.ReportAsync(item.Id,a,new("rolled_back",null,10,new DateTimeOffset(2024,1,2,3,4,5,TimeSpan.Zero)),CancellationToken.None)
                && (await policies.GetAsync(item.Id,CancellationToken.None))!.State=="rolled_back","The rollback is recorded.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_replacement_marks WHERE execution_id=@id AND byte_length=10",("id",item.Id))==1,"The restored original is also protected from automatic re-queueing.");
            Check(await policies.RequestRollbackAsync(item.Id,CancellationToken.None)=="not-restorable","A restored replacement cannot be restored twice.");

            // Grace period over: no rollback, only purge, and a media in use blocks the rollback.
            var expired=await PublicationRequestedAsync(true);
            status=await executions.GetAsync(expired.Id,CancellationToken.None);
            Check(await transfers.ConfirmAsync(expired.Id,a,new(expired.Lease.Epoch,Guid.NewGuid(),status!.Revision,$".encoding-results/{expired.Id:D}/result.mkv",expired.ResultSha,Replaced(expired.Id)),CancellationToken.None),"Second replacement confirmed.");
            Check((await policies.TasksAsync(a,CancellationToken.None)).All(task=>task.ExecutionId!=expired.Id),"Nothing is purged during the grace period.");
            var queued=Guid.NewGuid(); var secondFlow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            Check(await runtime.EnqueueAsync(new(queued,expired.Library,"source.mkv",secondFlow.Id,secondFlow.Version),secondFlow,CancellationToken.None),"Queue a second execution on the same media.");
            RuntimeLease? busyLease=null; for(var attempt=0;attempt<5 && busyLease?.ExecutionId!=queued;attempt++) busyLease=await runtime.ClaimAsync(a,CancellationToken.None);
            Check(busyLease?.ExecutionId==queued,"The second execution holds the media.");
            Check(await policies.RequestRollbackAsync(expired.Id,CancellationToken.None)=="busy","A media that is being processed cannot be restored concurrently.");
            await using(var elapse=db.CreateCommand("UPDATE source_replacements SET grace_until=clock_timestamp()-interval '1 second',state='replaced' WHERE execution_id=@id")){ elapse.Parameters.AddWithValue("id",expired.Id); await elapse.ExecuteNonQueryAsync(); }
            Check(await policies.RequestRollbackAsync(expired.Id,CancellationToken.None)=="not-restorable","A rollback is refused once the grace period is over.");
            Check((await policies.TasksAsync(a,CancellationToken.None)).Single(task=>task.ExecutionId==expired.Id).Action=="finalize","The backup becomes due for purge after the grace period.");
            Check(await policies.ReportAsync(expired.Id,a,new("failed","Fichier remplaçant absent"),CancellationToken.None) && (await policies.GetAsync(expired.Id,CancellationToken.None))!.State=="retained","A refused purge keeps the backup and flags it for manual action.");
            Check((await policies.TasksAsync(a,CancellationToken.None)).All(task=>task.ExecutionId!=expired.Id),"A retained backup is never retried automatically.");
            Console.WriteLine("PASS: replacement policy (disabled by default), forged-report refusal, grace period, rollback reservation, purge and monitoring protection.");
        }

        private async Task AutomaticMonitoringSkipsReplacedFileAsync(Guid library,Guid execution)
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow")) with { Id=Guid.NewGuid() };
            var repository=new FlowRepository(db); Check(await repository.SaveAsync(new(0,flow),CancellationToken.None) is not null,"Publish the monitoring flow.");
            var workflow=new LibraryWorkflowRepository(db,validator,new NodePresenceOptions(TimeSpan.FromHours(1)));
            Check(await workflow.AssignAsync(library,new(flow.Id,1,0),CancellationToken.None),"Assign the flow to the library.");
            var modified=new DateTimeOffset(2024,5,6,7,8,9,TimeSpan.Zero);
            var scan=Guid.NewGuid();
            await using(var file=db.CreateCommand("""
                INSERT INTO library_scans(id,library_id,media_type,state,completed_at) VALUES(@scan,@library,'video','completed',clock_timestamp());
                INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id,stable_since) VALUES(gen_random_uuid(),@library,'source.mkv',21,@modified,@scan,clock_timestamp()-interval '1 hour')
                """))
            { file.Parameters.AddWithValue("scan",scan); file.Parameters.AddWithValue("library",library); file.Parameters.AddWithValue("modified",modified); await file.ExecuteNonQueryAsync(); }
            var before=await ScalarAsync<long>("SELECT count(*) FROM executions");
            await workflow.EnqueueAutomaticallyAsync(library,CancellationToken.None);
            Check(await ScalarAsync<long>("SELECT count(*) FROM executions")==before,"The file produced by a replacement is not queued again by automatic monitoring.");
            await using(var changed=db.CreateCommand("UPDATE library_media SET modified_at=@modified WHERE library_id=@library")){ changed.Parameters.AddWithValue("library",library); changed.Parameters.AddWithValue("modified",modified.AddDays(1)); await changed.ExecuteNonQueryAsync(); }
            await workflow.EnqueueAutomaticallyAsync(library,CancellationToken.None);
            Check(await ScalarAsync<long>("SELECT count(*) FROM executions")==before+1,"A later modification of the file makes it eligible again.");
            await using(var cleanup=db.CreateCommand("UPDATE executions SET state='cancelled' WHERE state='queued' AND id<>@id")){ cleanup.Parameters.AddWithValue("id",execution); await cleanup.ExecuteNonQueryAsync(); }
        }

        public async Task WorkerLossFailoverAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"variable","base-variable",new(){["name"]=J("kept"),["value"]=J("7")}),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            context=context with { Variables=new Dictionary<string,JsonElement>{["kept"]=J(7)} };
            await CompleteAsync(id,a,first,"entry",context); await CompleteAsync(id,a,first,"variable",context);

            await ExpireAsync(id); // the worker running on node a stops renewing; node a stays reachable as the library access point.
            await using(var lost=db.CreateCommand("UPDATE cluster_nodes SET max_concurrent_tasks=0 WHERE id=@id")){ lost.Parameters.AddWithValue("id",a); await lost.ExecuteNonQueryAsync(); }
            Check(await runtime.ClaimAsync(a,CancellationToken.None) is null,"The lost worker cannot claim anything.");
            // The access node itself is unreachable: transfer workers cannot reach the media, so nobody takes it and nothing is lost.
            await using(var offline=db.CreateCommand("UPDATE cluster_nodes SET last_seen_at=clock_timestamp()-interval '2 hours' WHERE id=@id")){ offline.Parameters.AddWithValue("id",a); await offline.ExecuteNonQueryAsync(); }
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null && await runtime.ClaimAsync(c,CancellationToken.None) is null,"No transfer worker takes over while the library access node is offline.");
            await using(var back=db.CreateCommand("UPDATE cluster_nodes SET last_seen_at=clock_timestamp() WHERE id=@id")){ back.Parameters.AddWithValue("id",a); await back.ExecuteNonQueryAsync(); }
            var claims=await Task.WhenAll(runtime.ClaimAsync(b,CancellationToken.None),runtime.ClaimAsync(c,CancellationToken.None));
            Check(claims.Count(claim=>claim?.ExecutionId==id)==1,"Exactly one surviving worker takes over the abandoned execution.");
            var taker=claims.Single(claim=>claim?.ExecutionId==id)!; var takerNode=claims[0]?.ExecutionId==id?b:c; var other=takerNode==b?c:b;
            Check(taker.Epoch>first.Epoch && taker.Attempt==first.Attempt,"The takeover is the same attempt under a higher epoch.");
            Check(taker.Checkpoint?.NextNodeId=="end" && taker.Checkpoint.Context.Variables["kept"].GetInt32()==7,"The new worker resumes from the last durable checkpoint, without repeating completed steps.");
            Check(await runtime.RenewAsync(id,a,first.Epoch,CancellationToken.None) is null,"The lost worker is fenced when it comes back.");
            Check(await runtime.StartStepAsync(id,a,new(first.Epoch,"end"),CancellationToken.None) is null,"A returning worker cannot start a step with the old epoch.");
            Check(!await artifacts.AuthorizeAsync(id,a,first.Epoch,CancellationToken.None),"A returning worker cannot read artifacts with the old epoch.");
            Check(!await new RuntimeTransferRepository(db).FinishAsync(id,a,new(first.Epoch,Guid.NewGuid(),first.Revision,"failed","Stale worker",null,null),CancellationToken.None),"A returning worker cannot finish the execution.");
            Check(await runtime.ClaimAsync(other,CancellationToken.None) is null,"No other worker can take an execution that has a valid lease.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id AND lease_epoch=@epoch",("id",id),("epoch",taker.Epoch))==1,"A single reservation follows the new epoch.");

            // The replacement worker is lost in turn.
            await ExpireAsync(id);
            var third=await runtime.ClaimAsync(other,CancellationToken.None)??throw new InvalidOperationException("The remaining worker must take over.");
            Check(third.ExecutionId==id && third.Epoch>taker.Epoch,"Epochs increase on every takeover.");
            Check(await runtime.RenewAsync(id,takerNode,taker.Epoch,CancellationToken.None) is null,"The second lost worker is fenced as well.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE library_id=@library",("library",third.LibraryId!.Value))==1,"Only one execution holds the media at any time.");
            await CompleteAsync(id,other,third,"end",context);
            Check(await executions.CancelAsync(id,CancellationToken.None),"Clean up the failover fixture.");
            await ExpireAsync(id); await RecoverCancellationAsync(id);
            Console.WriteLine("PASS: worker loss — lease expiry, single takeover, checkpoint resume, stale-epoch fencing and successive failures.");
        }

        private async Task<Guid> QueueAsync(FlowDefinition flow)
        {
            var library=Guid.NewGuid(); var id=Guid.NewGuid();
            await using var command=db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@a,'direct',@root,true,true);
                INSERT INTO library_node_accesses(library_id,node_id,mode,can_read,can_write,transfer_source_node_id) VALUES(@library,@b,'transfer',true,true,@a),(@library,@c,'transfer',true,true,@a);
                """);
            command.Parameters.AddWithValue("library",library); command.Parameters.AddWithValue("name",library.ToString()); command.Parameters.AddWithValue("root",directory);
            command.Parameters.AddWithValue("a",a); command.Parameters.AddWithValue("b",b); command.Parameters.AddWithValue("c",c); await command.ExecuteNonQueryAsync();
            Check(await runtime.EnqueueAsync(new(id,library,"queued-"+id.ToString("N")+".mkv",flow.Id,flow.Version),flow,CancellationToken.None),"Queue fixture.");
            return id;
        }

        public async Task ServerLossFailoverAsync()
        {
            await ResetNodesAsync();
            await using var secondDb=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection) { SearchPath=schema }.ConnectionString);
            var secondConfigurations=new PluginConfigurationRepository(secondDb,DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory,"keys"))));
            var second=new RuntimeRepository(secondDb,eligibility,registry,new NodePresenceOptions(TimeSpan.FromHours(1)),secondConfigurations);
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var ids=new List<Guid>(); for(var index=0;index<8;index++) ids.Add(await QueueAsync(flow));

            // A server dies while it holds the row locks of a claim in progress.
            int pid; NpgsqlConnection holder=await secondDb.OpenConnectionAsync(); NpgsqlTransaction hold=await holder.BeginTransactionAsync();
            await using(var backend=new NpgsqlCommand("SELECT pg_backend_pid()",holder,hold)) pid=(int)(await backend.ExecuteScalarAsync())!;
            await using(var lockRow=new NpgsqlCommand("SELECT execution_id FROM execution_work WHERE execution_id=@id FOR UPDATE",holder,hold)){ lockRow.Parameters.AddWithValue("id",ids[0]); await lockRow.ExecuteScalarAsync(); }
            var duringCrash=new List<Guid>();
            for(var attempt=0;attempt<ids.Count+2;attempt++) if(await runtime.ClaimAsync(b,CancellationToken.None) is { } leased && ids.Contains(leased.ExecutionId)) duringCrash.Add(leased.ExecutionId);
            Check(!duringCrash.Contains(ids[0]) && duringCrash.Distinct().Count()==duringCrash.Count,"Work locked by a server in the middle of a claim is skipped, never duplicated.");
            await using(var kill=db.CreateCommand("SELECT pg_terminate_backend(@pid)")){ kill.Parameters.AddWithValue("pid",pid); Check((bool)(await kill.ExecuteScalarAsync())!,"Terminate the crashed server's connection."); }
            try{ await holder.DisposeAsync(); }catch(Exception){ }
            var recovered=await runtime.ClaimAsync(c,CancellationToken.None);
            Check(recovered is not null && ids.Contains(recovered.ExecutionId),"After the crash the surviving server hands out the work that was locked.");

            // Two live servers claim concurrently on behalf of different workers: each execution has exactly one owner.
            var claimed=new List<RuntimeLease>(); if(recovered is not null) claimed.Add(recovered);
            var remaining=ids.Count-duringCrash.Count-claimed.Count;
            for(var round=0;round<ids.Count && remaining>0;round++)
            {
                var results=await Task.WhenAll(runtime.ClaimAsync(c,CancellationToken.None),second.ClaimAsync(b,CancellationToken.None),second.ClaimAsync(c,CancellationToken.None));
                foreach(var lease in results.Where(lease=>lease is not null && ids.Contains(lease.ExecutionId))) { claimed.Add(lease!); remaining--; }
            }
            var owners=claimed.Select(lease=>lease.ExecutionId).Concat(duringCrash).ToList();
            Check(owners.Count==ids.Count && owners.Distinct().Count()==ids.Count,"Every queued execution is leased exactly once across both servers.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM (SELECT execution_id FROM execution_work WHERE execution_id=ANY(@ids) GROUP BY execution_id HAVING count(*) FILTER(WHERE lease_until>clock_timestamp())>1) x",("ids",ids.ToArray()))==0,"No execution has two active leases.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations m JOIN execution_work w ON w.execution_id=m.execution_id AND w.lease_epoch=m.lease_epoch WHERE m.execution_id=ANY(@ids)",("ids",ids.ToArray()))==ids.Count,"Each leased media holds one matching reservation.");

            // A server dies after the access node published and before it could acknowledge the confirmation.
            var item=await PublicationRequestedAsync(true);
            var confirm=new ConfirmRuntimePublication(item.Lease.Epoch,Guid.NewGuid(),(await executions.GetAsync(item.Id,CancellationToken.None))!.Revision,$".encoding-results/{item.Id:D}/result.mkv",item.ResultSha,Replaced(item.Id));
            Check(await new RuntimeTransferRepository(db).ConfirmAsync(item.Id,a,confirm,CancellationToken.None),"The first server records the replacement.");
            Check(!await new RuntimeTransferRepository(secondDb).ConfirmAsync(item.Id,a,confirm,CancellationToken.None),"The agent's retry through the second server is rejected cleanly.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM source_replacements WHERE execution_id=@id",("id",item.Id))==1 && (await new SourceReplacementRepository(secondDb).GetAsync(item.Id,CancellationToken.None))?.State=="replaced",
                "Any server reads the same single, durable replacement record.");
            var rollbacks=await Task.WhenAll(new SourceReplacementRepository(db).RequestRollbackAsync(item.Id,CancellationToken.None),new SourceReplacementRepository(secondDb).RequestRollbackAsync(item.Id,CancellationToken.None));
            Check(rollbacks.Count(result=>result=="ok")==1,"Two servers cannot both start a rollback.");
            Console.WriteLine("PASS: server loss — locks released on crash, no double lease across servers, idempotent confirmation and single rollback.");
        }
    }
}
