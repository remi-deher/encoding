using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Encoding.Api.Infrastructure.PeerCluster;

namespace Encoding.Api.Infrastructure.Data;

/// <summary>Preserves repository transactions, including generated values, and confirms them through Raft before local commit.</summary>
public sealed class PeerDataSource(PeerDatabaseCoordinator coordinator) : DbDataSource
{
    public DbDataSource Inner => coordinator.Source;
    public override string ConnectionString => Inner.ConnectionString;
    protected override DbConnection CreateDbConnection() => new PeerConnection(Inner.CreateConnection(), coordinator);
    protected override void Dispose(bool disposing) { if (disposing) Inner.Dispose(); base.Dispose(disposing); }
    protected override ValueTask DisposeAsyncCore() => Inner.DisposeAsync();
}

public sealed class PeerConnection(DbConnection inner, PeerDatabaseCoordinator coordinator) : DbConnection
{
    public DbConnection Inner => inner;
    internal PeerTransaction? Transaction { get; set; }
    [AllowNull] public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
    public override string Database => inner.Database;
    public override string DataSource => inner.DataSource;
    public override string ServerVersion => inner.ServerVersion;
    public override ConnectionState State => inner.State;
    public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
    public override void Open() => inner.Open();
    public override Task OpenAsync(CancellationToken ct) => inner.OpenAsync(ct);
    public override void Close() => inner.Close();
    public override Task CloseAsync() => inner.CloseAsync();
    protected override DbCommand CreateDbCommand() => new PeerCommand(inner.CreateCommand(), this, coordinator);
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginDbTransactionAsync(isolationLevel, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken ct)
    {
        if (!await coordinator.WriteGate.WaitAsync(TimeSpan.FromSeconds(30), ct)) throw new TimeoutException("Transaction partagée occupée.");
        try
        {
            var term = await coordinator.PrepareTransactionAsync(ct);
            return Transaction = new PeerTransaction(await inner.BeginTransactionAsync(isolationLevel, ct), this, coordinator, term);
        }
        catch { coordinator.WriteGate.Release(); throw; }
    }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
}

public sealed class PeerTransaction(DbTransaction inner, PeerConnection connection, PeerDatabaseCoordinator coordinator, long? preparedTerm) : DbTransaction
{
    public DbTransaction Inner => inner;
    private readonly Guid id = Guid.NewGuid();
    private CancellationTokenSource? lease;
    private bool completed;
    private bool capturing;
    protected override DbConnection DbConnection => connection;
    public override IsolationLevel IsolationLevel => inner.IsolationLevel;

    public async Task CaptureAsync(CancellationToken ct)
    {
        if (capturing) { lease!.Token.ThrowIfCancellationRequested(); return; }
        coordinator.CheckPreparedTerm(preparedTerm);
        lease = coordinator.RequireLease(ct);
        await PeerDatabaseSchema.ExecuteAsync(connection.Inner, inner,
            "UPDATE peer_replication_context SET active=1 WHERE singleton=1", ct);
        capturing = true;
    }

    public override void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override async Task CommitAsync(CancellationToken ct = default)
    {
        if (completed) throw new InvalidOperationException("Transaction terminée.");
        try
        {
            if (capturing)
            {
                lease!.Token.ThrowIfCancellationRequested();
                await coordinator.PublishAsync(id, connection.Inner, inner, ct);
            }
            // A committed Raft transaction must be materialized even if the HTTP caller disconnects now.
            await inner.CommitAsync(CancellationToken.None);
        }
        catch
        {
            if (capturing) coordinator.MarkUncertain();
            await inner.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally { Complete(); }
    }
    public override void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override async Task RollbackAsync(CancellationToken ct = default)
    {
        if (completed) return;
        try { await inner.RollbackAsync(ct); } finally { Complete(); }
    }
    private void Complete()
    {
        if (completed) return;
        completed = true;
        lease?.Dispose();
        connection.Transaction = null;
        coordinator.WriteGate.Release();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (!completed) Rollback(); inner.Dispose(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!completed) await RollbackAsync();
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

public sealed class PeerCommand(DbCommand inner, PeerConnection connection, PeerDatabaseCoordinator coordinator) : DbCommand
{
    private PeerTransaction? transaction;
    [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
    public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
    public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    protected override DbConnection? DbConnection { get => connection; set { if (value is not null && value != connection) throw new InvalidOperationException("Connexion immuable."); } }
    protected override DbParameterCollection DbParameterCollection => inner.Parameters;
    protected override DbTransaction? DbTransaction
    {
        get => transaction;
        set { transaction = (PeerTransaction?)value; inner.Transaction = transaction?.Inner; }
    }
    protected override DbParameter CreateDbParameter() => inner.CreateParameter();
    public override void Cancel() => inner.Cancel();
    public override void Prepare() => inner.Prepare();
    private async Task<PeerTransaction?> PrepareWriteAsync(CancellationToken ct)
    {
        if (!coordinator.IsSharedWrite(CommandText)) return null;
        var existing = transaction ?? connection.Transaction;
        var created = existing is null ? (PeerTransaction)await connection.BeginTransactionAsync(ct) : null;
        var active = existing ?? created!;
        try { await active.CaptureAsync(ct); inner.Transaction = active.Inner; return created; }
        catch { if (created is not null) await created.DisposeAsync(); throw; }
    }
    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var automatic = await PrepareWriteAsync(ct);
        try
        {
            var result = await action(ct);
            if (automatic is not null) await automatic.CommitAsync(ct);
            return result;
        }
        finally
        {
            if (automatic is not null) { inner.Transaction = null; await automatic.DisposeAsync(); }
        }
    }
    public override int ExecuteNonQuery() => ExecuteNonQueryAsync().GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync().GetAwaiter().GetResult();
    public override Task<int> ExecuteNonQueryAsync(CancellationToken ct) => ExecuteAsync(inner.ExecuteNonQueryAsync, ct);
    public override Task<object?> ExecuteScalarAsync(CancellationToken ct) => ExecuteAsync(inner.ExecuteScalarAsync, ct);
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct)
    {
        var automatic = await PrepareWriteAsync(ct);
        try
        {
            var reader = await inner.ExecuteReaderAsync(behavior & ~CommandBehavior.CloseConnection, ct);
            return new TransactionReader(reader, automatic, connection, (behavior & CommandBehavior.CloseConnection) != 0);
        }
        catch { if (automatic is not null) await automatic.DisposeAsync(); throw; }
    }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
}
