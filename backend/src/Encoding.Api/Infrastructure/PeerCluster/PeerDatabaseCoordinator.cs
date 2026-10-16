using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed class PeerWriteUnavailableException(string message) : InvalidOperationException(message);
public sealed record PeerLocalDatabase(DbDataSource Source);

/// <summary>Coordinates the existing SQL repositories with a quorum-backed transaction journal.</summary>
public sealed partial class PeerDatabaseCoordinator(PeerLocalDatabase local, IServiceProvider services, PeerSecretProtection protection)
{
    public DbDataSource Source => local.Source;
    public PeerDatabaseSchema Schema { get; } = new(local.Source, protection.KeyId);
    public SemaphoreSlim WriteGate { get; } = new(1, 1);
    public bool Enabled { get; private set; }
    public PeerDatabaseImage? Image { get; private set; }
    public bool NeedsRepair { get; private set; }
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> pending = new();
    private readonly object sync = new();
    private readonly SemaphoreSlim projectionGate = new(1, 1);
    private PeerConsensusService Consensus => services.GetRequiredService<PeerConsensusService>();

    public async Task InitializeAsync(CancellationToken ct)
    {
        await Schema.InitializeAsync(ct);
        Enabled = true;
    }

    public bool IsSharedWrite(string sql)
    {
        if (!Enabled) return false;
        if (Ddl().IsMatch(sql)) throw new InvalidOperationException("Les migrations exigent un arrêt coordonné des pairs.");
        var shared = false;
        foreach (System.Text.RegularExpressions.Match match in Mutations().Matches(sql))
        {
            var name = match.Groups[1].Value;
            if (Schema.Tables.Any(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))) shared = true;
            else if (!PeerDatabaseSchema.LocalTables.Contains(name))
                throw new InvalidOperationException("Une écriture vise une table dont la politique de réplication est inconnue.");
        }
        return shared;
    }

    public CancellationTokenSource RequireLease(CancellationToken ct)
    {
        if (Image is null || NeedsRepair || !Consensus.TryGetLeaderLease(out var lease))
            throw new PeerWriteUnavailableException("Le pair ne peut pas confirmer une écriture : leader, quorum ou projection indisponible.");
        return CancellationTokenSource.CreateLinkedTokenSource(ct, lease);
    }

    public Task<long?> PrepareTransactionAsync(CancellationToken ct) =>
        Enabled && Image is not null ? Consensus.PrepareDatabaseTransactionAsync(ct) : Task.FromResult<long?>(null);

    public void CheckPreparedTerm(long? term)
    {
        if (term is null || Consensus.CurrentTerm != term)
            throw new PeerWriteUnavailableException("La transaction SQL doit être reprise après synchronisation du leader.");
    }

    public async Task PublishAsync(Guid id, DbConnection connection, DbTransaction tx, CancellationToken ct)
    {
        var changes = await Schema.ReadChangesAsync(connection, tx, ct);
        if (changes.Length == 0)
        {
            await PeerDatabaseSchema.ExecuteAsync(connection, tx,
                "UPDATE peer_replication_context SET active=0 WHERE singleton=1", ct);
            return;
        }
        using var lease = RequireLease(ct);
        var revision = (Image?.Revision ?? 0) + 1;
        services.GetService<ILogger<PeerDatabaseCoordinator>>()?.LogDebug("Proposition de la transaction {Id}, révision {Revision}.", id, revision);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.TryAdd(id, applied);
        try
        {
            await Consensus.ReplicateAsync(new(id, "database/transaction",
                JsonSerializer.SerializeToElement(new PeerDatabaseTransaction(1, revision, Schema.Fingerprint, changes))), lease.Token);
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        catch { NeedsRepair = true; throw; }
        finally { pending.TryRemove(id, out _); }
        await PeerDatabaseSchema.ExecuteAsync(connection, tx,
            "DELETE FROM peer_replication_changes; UPDATE peer_replication_context SET active=0 WHERE singleton=1", ct);
    }

    public async Task ApplyAsync(Guid id, PeerDatabaseTransaction change, CancellationToken ct)
    {
        await projectionGate.WaitAsync(ct);
        try
        {
            await ApplyCoreAsync(id, change, ct);
            services.GetService<ILogger<PeerDatabaseCoordinator>>()?.LogDebug("Transaction {Id} appliquée à la révision {Revision}.", id, change.Revision);
            NeedsRepair = false;
            if (pending.TryGetValue(id, out var applied)) applied.TrySetResult();
        }
        catch (Exception exception)
        {
            NeedsRepair = true;
            if (pending.TryGetValue(id, out var applied)) applied.TrySetException(exception);
            throw;
        }
        finally { projectionGate.Release(); }
    }

    private async Task ApplyCoreAsync(Guid id, PeerDatabaseTransaction change, CancellationToken ct)
    {
        if (change.Version != 1 || change.Schema != Schema.Fingerprint)
            throw new InvalidDataException("Version ou schéma de transaction incompatible.");
        if (Image is not null && change.Revision <= Image.Revision) return;
        if (change.Initial is null && change.Revision != (Image?.Revision ?? 0) + 1)
            throw new InvalidDataException("Révision de transaction non contiguë.");
        if (!pending.ContainsKey(id))
        {
            await using var connection = await Source.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            if (change.Initial is not null) await Schema.RestoreAsync(connection, tx, change.Initial, ct);
            else await Schema.ApplyChangesAsync(connection, tx, change.Changes, ct);
            await tx.CommitAsync(ct);
        }
        lock (sync)
        {
            var image = change.Initial ?? Image ?? throw new InvalidDataException("L’état initial manque.");
            // Snapshot values are immutable once published to the state machine.
            var tables = image.Tables;
            foreach (var row in change.Changes)
            {
                var table = Schema.Tables.Single(table => table.Name == row.Table);
                var key = PeerDatabaseSchema.Key(table, row.Key);
                var records = row.Operation == "DELETE" ? tables[row.Table].Remove(key)
                    : tables[row.Table].SetItem(key, row.Row ?? throw new InvalidDataException("Ligne absente."));
                tables = tables.SetItem(row.Table, records);
            }
            Image = new(change.Revision, change.Schema, tables);
        }
    }

    public async Task RestoreAsync(PeerDatabaseImage image, CancellationToken ct)
    {
        await projectionGate.WaitAsync(ct);
        try { await RestoreCoreAsync(image, ct); }
        finally { projectionGate.Release(); }
    }

    private async Task RestoreCoreAsync(PeerDatabaseImage image, CancellationToken ct)
    {
        services.GetService<ILogger<PeerDatabaseCoordinator>>()?.LogDebug("Restauration de la projection à la révision {Revision}.", image.Revision);
        await using var connection = await Source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Schema.RestoreAsync(connection, tx, image, ct);
        await tx.CommitAsync(ct);
        Image = image;
        NeedsRepair = false;
    }

    public void MarkUncertain() => NeedsRepair = true;

    public async Task BootstrapAsync(CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct);
        try
        {
            if (Image is not null) return;
            await using var connection = await Source.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            await protection.RewrapAsync(connection, tx, ct);
            var image = await Schema.CaptureAsync(connection, tx, 1, ct);
            await tx.RollbackAsync(ct);
            await Consensus.ReplicateAsync(new(Guid.NewGuid(), "database/transaction",
                JsonSerializer.SerializeToElement(new PeerDatabaseTransaction(1, 1, Schema.Fingerprint, [], image))), ct);
        }
        finally { WriteGate.Release(); }
    }

    public async Task RepairAsync(CancellationToken ct)
    {
        // A former leader can need repair after an uncertain local commit. Followers may rebuild
        // their local projection from committed state without obtaining a write lease.
        if (!NeedsRepair || Image is null) return;
        await WriteGate.WaitAsync(ct);
        try
        {
            await projectionGate.WaitAsync(ct);
            try { if (Image is { } image) await RestoreCoreAsync(image, ct); }
            finally { projectionGate.Release(); }
        }
        finally { WriteGate.Release(); }
    }

    [GeneratedRegex(@"\b(?:INSERT\s+(?:OR\s+\w+\s+)?INTO|UPDATE(?!\s+(?:SET|OF|SKIP|NOWAIT)\b)|DELETE\s+FROM)\s+(?:""?[a-zA-Z_][a-zA-Z0-9_]*""?\.)?""?([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Mutations();
    [GeneratedRegex(@"^\s*(?:CREATE|DROP|ALTER|TRUNCATE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Ddl();
}

public sealed class PeerDatabaseBootstrapService(PeerClusterOptions options, PeerDatabaseCoordinator database,
    PeerConsensusService consensus, ILogger<PeerDatabaseBootstrapService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.Bootstrap && database.Image is null && consensus.GetStatus() is { IsLeader: true, HasQuorum: true })
                    await database.BootstrapAsync(stoppingToken);
                await database.RepairAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Initialisation ou réparation de la projection répliquée impossible."); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
