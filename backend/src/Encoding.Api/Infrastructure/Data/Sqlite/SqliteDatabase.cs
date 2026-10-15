using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Encoding.Api.Infrastructure.Data.Sqlite;

/// <summary>
/// Source de données SQLite : mêmes types ADO.NET génériques que PostgreSQL (architecture 0043).
/// Les connexions traduisent le SQL PostgreSQL à l'exécution et ouvrent leurs transactions en écriture immédiate,
/// ce qui remplace les verrous de lignes et les verrous consultatifs (un seul écrivain à la fois).
/// </summary>
public sealed class SqliteDataSource(string connectionString) : DbDataSource
{
    public override string ConnectionString { get; } = connectionString;

    protected override DbConnection CreateDbConnection() => new SqliteConnectionWrapper(new SqliteConnection(ConnectionString));
}

public sealed class SqliteConnectionWrapper(SqliteConnection inner) : DbConnection
{
    internal SqliteConnection Inner { get; } = inner;

    [AllowNull] public override string ConnectionString { get => Inner.ConnectionString; set => Inner.ConnectionString = value; }
    public override string Database => Inner.Database;
    public override string DataSource => Inner.DataSource;
    public override string ServerVersion => Inner.ServerVersion;
    public override ConnectionState State => Inner.State;

    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);
    public override void Close() => Inner.Close();

    public override void Open()
    {
        Inner.Open();
        Initialize();
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        await Inner.OpenAsync(cancellationToken);
        await using var command = Inner.CreateCommand();
        command.CommandText = ConnectionPragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string ConnectionPragmas = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000; PRAGMA synchronous = NORMAL;";

    private void Initialize()
    {
        using var command = Inner.CreateCommand();
        command.CommandText = ConnectionPragmas;
        command.ExecuteNonQuery();
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new SqliteTransactionWrapper(this, Inner.BeginTransaction(deferred: false));

    protected override DbCommand CreateDbCommand() => new SqliteCommandWrapper { WrappedConnection = this };

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class SqliteTransactionWrapper(SqliteConnectionWrapper connection, SqliteTransaction inner) : DbTransaction
{
    internal SqliteTransaction Inner { get; } = inner;
    protected override DbConnection DbConnection => connection;
    public override IsolationLevel IsolationLevel => Inner.IsolationLevel;
    public override void Commit() => Inner.Commit();
    public override void Rollback() => Inner.Rollback();
    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class SqliteCommandWrapper : DbCommand
{
    private static readonly ConcurrentDictionary<string, string> Translations = new(StringComparer.Ordinal);
    private readonly SqliteParameterCollection parameters = new SqliteCommand().Parameters;
    private SqliteCommand? executing;

    internal SqliteConnectionWrapper? WrappedConnection { get; set; }
    private SqliteTransactionWrapper? wrappedTransaction;

    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get => WrappedConnection; set => WrappedConnection = (SqliteConnectionWrapper?)value; }
    protected override DbParameterCollection DbParameterCollection => parameters;
    protected override DbTransaction? DbTransaction { get => wrappedTransaction; set => wrappedTransaction = (SqliteTransactionWrapper?)value; }

    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new SqliteParameter();

    private static readonly bool Trace = Environment.GetEnvironmentVariable("ENCODING_SQLITE_TRACE") == "1";

    private SqliteCommand Build()
    {
        executing?.Dispose();
        var connection = WrappedConnection ?? throw new InvalidOperationException("La commande n'est reliée à aucune connexion.");
        var command = connection.Inner.CreateCommand();
        command.Transaction = wrappedTransaction?.Inner;
        command.CommandTimeout = CommandTimeout;
        var sql = Translations.GetOrAdd(CommandText, text => { try { return SqliteTranslator.Translate(text); } catch (FormatException exception) { throw new FormatException($"{exception.Message} [SQL: {text}]"); } });
        var arrays = SqliteTranslator.ArrayParameters(sql);
        foreach (SqliteParameter parameter in parameters)
        {
            var name = parameter.ParameterName.TrimStart('@', ':', '$');
            if (arrays.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var items = ((IEnumerable)(parameter.Value ?? Array.Empty<object>())).Cast<object?>().ToList();
                sql = SqliteTranslator.ExpandArray(sql, name, items.Count);
                for (var i = 0; i < items.Count; i++) command.Parameters.AddWithValue($"@{name}_{i}", Convert(items[i]));
            }
            else command.Parameters.AddWithValue("@" + name, Convert(parameter.Value));
        }
        command.CommandText = sql;
        if (Trace) Console.Error.WriteLine("SQLTRACE " + sql.Replace('\n', ' ') + " :: " + string.Join(", ", command.Parameters.Cast<SqliteParameter>().Select(p => p.ParameterName + "=" + p.Value)));
        return executing = command;
    }

    /// <summary>Valeur SQLite d'un paramètre : instants en texte UTC de largeur fixe, durées en modificateurs, tableaux en JSON.</summary>
    internal static object Convert(object? value) => value switch
    {
        null or DBNull => DBNull.Value,
        DateTimeOffset instant => Instant(instant.UtcDateTime),
        DateTime instant => Instant(instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
        TimeSpan span => (span < TimeSpan.Zero ? "-" : "+") + Math.Abs(span.TotalSeconds).ToString("0.######", CultureInfo.InvariantCulture) + " seconds",
        Guid id => id.ToString("D"),
        string or byte[] => value,
        IEnumerable items => JsonSerializer.Serialize(items.Cast<object?>().Select(Convert).ToList()),
        _ => value
    };

    public static string Instant(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "Z";

    private T Run<T>(Func<SqliteCommand, T> action)
    {
        var command = Build();
        try { return action(command); }
        catch (SqliteException exception) { throw new SqliteException($"{exception.Message} [SQL: {command.CommandText}]", exception.SqliteErrorCode, exception.SqliteExtendedErrorCode); }
    }

    private async Task<T> RunAsync<T>(Func<SqliteCommand, Task<T>> action)
    {
        var command = Build();
        try { return await action(command); }
        catch (SqliteException exception) { throw new SqliteException($"{exception.Message} [SQL: {command.CommandText}]", exception.SqliteErrorCode, exception.SqliteExtendedErrorCode); }
    }

    public override int ExecuteNonQuery() => Run(command => command.ExecuteNonQuery());
    public override object? ExecuteScalar() => Run(command => command.ExecuteScalar());
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Run(command => (DbDataReader)command.ExecuteReader(behavior));
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => RunAsync(command => command.ExecuteNonQueryAsync(cancellationToken));
    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => RunAsync(command => command.ExecuteScalarAsync(cancellationToken));
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        await RunAsync(async command => (DbDataReader)await command.ExecuteReaderAsync(behavior, cancellationToken));

    protected override void Dispose(bool disposing)
    {
        if (disposing) executing?.Dispose();
        base.Dispose(disposing);
    }
}
