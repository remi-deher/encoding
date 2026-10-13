using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Encoding.Api.Flows;

/// <summary>Replays the transactional outbox and periodically checks completed snapshots.</summary>
public sealed class FlowBackupService(NpgsqlDataSource dataSource, IConfiguration configuration, ILogger<FlowBackupService> logger) : BackgroundService
{
    private readonly string directory = Path.GetFullPath(configuration["FlowBackups:Directory"] ?? Path.Combine(AppContext.BaseDirectory,"flow-backups"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = 0;
                while (processed++ < 100 && await ProcessOneAsync(stoppingToken)) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception,"Impossible de traiter la file de sauvegardes des flows."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        Guid id; int version; string document;
        await using (var command = new NpgsqlCommand("""
            SELECT b.flow_id,b.version,v.definition::text FROM flow_backup_jobs b
            JOIN flow_versions v ON v.flow_id=b.flow_id AND v.version=b.version
            WHERE b.next_attempt_at <= clock_timestamp() ORDER BY b.next_attempt_at,b.flow_id,b.version
            LIMIT 1 FOR UPDATE OF b SKIP LOCKED
            """,connection,tx))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false;
            id=reader.GetGuid(0); version=reader.GetInt32(1); document=reader.GetString(2);
        }
        var status="ready";
        try { await WriteSnapshotAsync(id,version,document,ct); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { status="failed"; logger.LogWarning(exception,"Sauvegarde impossible pour le flow {FlowId}, version {Version}.",id,version); }
        await using var update = new NpgsqlCommand("""
            UPDATE flow_backup_jobs SET status=@status,checked_at=clock_timestamp(),
            next_attempt_at=clock_timestamp()+@delay WHERE flow_id=@id AND version=@version
            """,connection,tx);
        update.Parameters.AddWithValue("id",id); update.Parameters.AddWithValue("version",version);
        update.Parameters.AddWithValue("status",status); update.Parameters.AddWithValue("delay",status=="ready" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30));
        await update.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return true;
    }

    private async Task WriteSnapshotAsync(Guid id,int version,string document,CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var target=Path.Combine(directory,$"{id:D}.v{version}.json");
        var bytes=System.Text.Encoding.UTF8.GetBytes(document);
        async Task VerifyAsync()
        {
            var existing=await File.ReadAllBytesAsync(target,ct);
            if (!SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                throw new IOException("L’instantané existant ne correspond pas à la version en base. Il est conservé pour diagnostic.");
        }
        if (File.Exists(target)) { await VerifyAsync(); return; }
        var temporary=Path.Combine(directory,$".{id:D}.{version}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough | FileOptions.Asynchronous))
            { await stream.WriteAsync(bytes,ct); await stream.FlushAsync(ct); stream.Flush(true); }
            try { File.Move(temporary,target,false); }
            catch (IOException) when (File.Exists(target)) { await VerifyAsync(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
