using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task<object> MeasureCoordinationAsync(bool concurrentCapture)
        {
            var library = Guid.NewGuid();
            await using (var seed = db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write)
                    VALUES(@library,@worker,'direct',@root,true,true);
                """))
            {
                seed.Parameters.AddWithValue("library", library); seed.Parameters.AddWithValue("name", library.ToString());
                seed.Parameters.AddWithValue("worker", a); seed.Parameters.AddWithValue("root", directory);
                await seed.ExecuteNonQueryAsync();
            }
            var flow = Chain(Node(registry, "entry", "base-input-file"), Node(registry, "end", "base-complete-flow"));
            for (var i = 0; i < 128; i++)
                Check(await runtime.EnqueueAsync(new(Guid.NewGuid(), library, $"sample-{i}.mkv", flow.Id, flow.Version), flow, CancellationToken.None), "Queue measurement workload.");
            await new ClusterReconciliationRepository(db).EnsureIdentityAsync(CancellationToken.None);
            var cluster = await new ClusterIdentityRepository(db).GetAsync(CancellationToken.None);
            using var keys = new ClusterDataKeyStore(cluster.Id, RandomNumberGenerator.GetBytes(32));
            var capture = new ClusterEntityReconciliation(db, keys, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
            await capture.CaptureLocalChangesAsync(CancellationToken.None);
            var writer = ((SqliteDataSource)db).Writer;
            var beforeIdle = writer.Snapshot();
            var idle = Stopwatch.StartNew();
            for (var i = 0; i < 8; i++) Check(await capture.CaptureLocalChangesAsync(CancellationToken.None) == 0, "Idle capture has no events.");
            idle.Stop();
            Check(writer.Snapshot().Admitted == beforeIdle.Admitted, "Idle capture does not acquire writer admission.");

            var claims = new ConcurrentBag<double>();
            var renewals = new ConcurrentBag<double>();
            var executionIds = new ConcurrentBag<Guid>();
            using var start = new ManualResetEventSlim();
            using var ready = new CountdownEvent(concurrentCapture ? 9 : 8);
            Task Launch(Func<Task> work) => Task.Factory.StartNew(async () =>
            {
                ready.Signal();
                start.Wait();
                await work();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            var jobs = Enumerable.Range(0, 8).Select(_ => Launch(async () =>
            {
                for (var i = 0; i < 4; i++)
                {
                    var claimStarted = Stopwatch.GetTimestamp();
                    var lease = await runtime.ClaimAsync(a, CancellationToken.None);
                    claims.Add(Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds);
                    Check(lease is not null, "Every measurement claim succeeds.");
                    executionIds.Add(lease!.ExecutionId);
                    for (var renewal = 0; renewal < 4; renewal++)
                    {
                        var renewStarted = Stopwatch.GetTimestamp();
                        Check(await runtime.RenewAsync(lease.ExecutionId, a, lease.Epoch, CancellationToken.None) is not null, "Renewal succeeds under capture load.");
                        renewals.Add(Stopwatch.GetElapsedTime(renewStarted).TotalMilliseconds);
                    }
                }
            })).ToList();
            if (concurrentCapture) jobs.Add(Launch(async () =>
            {
                for (var i = 0; i < 16; i++)
                {
                    await using var change = db.CreateCommand("UPDATE libraries SET name=@name WHERE id=@library");
                    change.Parameters.AddWithValue("library", library);
                    change.Parameters.AddWithValue("name", library + "-" + i);
                    await change.ExecuteNonQueryAsync();
                    await capture.CaptureLocalChangesAsync(CancellationToken.None);
                }
            }));
            Check(ready.Wait(TimeSpan.FromSeconds(5)), "All synthetic clients are ready before measurement starts.");
            var before = writer.Snapshot();
            var elapsed = Stopwatch.StartNew();
            start.Set();
            await Task.WhenAll(jobs);
            elapsed.Stop();
            var after = writer.Snapshot();
            Check(executionIds.Distinct().Count() == 32 && renewals.Count == 128, "No claim is duplicated and all renewals complete.");
            Check(after.Rejected == before.Rejected && after.Expired == before.Expired, "The workload neither saturates nor expires writer admission.");
            await capture.CaptureLocalChangesAsync(CancellationToken.None);

            static object Distribution(IEnumerable<double> values)
            {
                var ordered = values.Order().ToArray();
                double At(double percentile) => Math.Round(ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1], 3);
                return new { count = ordered.Length, p50_ms = At(.5), p95_ms = At(.95), p99_ms = At(.99), max_ms = Math.Round(ordered[^1], 3) };
            }
            return new
            {
                concurrent_capture = concurrentCapture, clients = 8, active_worker_nodes = 1, queued = 128,
                elapsed_ms = Math.Round(elapsed.Elapsed.TotalMilliseconds, 3), idle_capture_8_ms = Math.Round(idle.Elapsed.TotalMilliseconds, 3),
                idle_writer_admissions = 0, claim = Distribution(claims), renew = Distribution(renewals),
                writer_admissions = after.Admitted - before.Admitted,
                writer_wait_ms = Math.Round((after.WaitSeconds - before.WaitSeconds) * 1000, 3),
                writer_hold_ms = Math.Round((after.HoldSeconds - before.HoldSeconds) * 1000, 3),
                writer_long_holds = after.LongHolds - before.LongHolds,
                writer_rejections = after.Rejected - before.Rejected, writer_expirations = after.Expired - before.Expired
            };
        }
    }
}
