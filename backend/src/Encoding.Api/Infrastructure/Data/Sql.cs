using System.Data.Common;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace Encoding.Api.Infrastructure.Data;

/// <summary>Types de paramètres dont le sens dépend du fournisseur (JSON, binaire, tableaux).</summary>
public enum SqlKind { Text, Json, Bytes, Integer, Decimal, Uuid, TextArray }

/// <summary>
/// Seul point du backend qui connaît le fournisseur de base de données.
/// Les dépôts manipulent des types ADO.NET génériques (DbConnection, DbCommand…) ; voir l'architecture 0043.
/// </summary>
public static class Sql
{
    public static DbCommand Command(string sql, DbConnection connection, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static bool IsSqlite(DbConnection connection) => connection is SqliteConnectionWrapper
        || connection is PeerConnection peer && IsSqlite(peer.Inner);
    public static bool IsSqlite(DbDataSource dataSource) => dataSource is SqliteDataSource
        || dataSource is PeerDataSource peer && IsSqlite(peer.Inner);

    public static DbParameter Parameter(string name, SqlKind kind, object? value, bool sqlite = false)
    {
        if (sqlite) return new SqliteParameter(name, value ?? DBNull.Value);
        var parameter = new NpgsqlParameter { ParameterName = name, Value = value ?? DBNull.Value };
        switch (kind)
        {
            case SqlKind.Json: parameter.NpgsqlDbType = NpgsqlDbType.Jsonb; break;
            case SqlKind.Bytes: parameter.NpgsqlDbType = NpgsqlDbType.Bytea; break;
            case SqlKind.Text: parameter.NpgsqlDbType = NpgsqlDbType.Text; break;
            case SqlKind.Integer: parameter.NpgsqlDbType = NpgsqlDbType.Integer; break;
            case SqlKind.Decimal: parameter.NpgsqlDbType = NpgsqlDbType.Numeric; break;
            case SqlKind.Uuid: parameter.NpgsqlDbType = NpgsqlDbType.Uuid; break;
            case SqlKind.TextArray: parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text; break;
        }
        return parameter;
    }

    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, object? value)
    {
        DbParameter parameter = parameters is SqliteParameterCollection
            ? new SqliteParameter(name, value ?? DBNull.Value)
            : new NpgsqlParameter { ParameterName = name, Value = value ?? DBNull.Value };
        parameters.Add(parameter);
        return parameter;
    }

    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, SqlKind kind, object? value)
    {
        var parameter = Parameter(name, kind, value, parameters is SqliteParameterCollection);
        parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Violation d'unicité signalée par le fournisseur.</summary>
    public static string[] GetStringArray(this DbDataReader reader, int ordinal) => ToStringArray(reader.GetValue(ordinal)) ?? [];

    public static string[]? ToStringArray(object? value) => value switch
    {
        string[] array => array,
        string json => System.Text.Json.JsonSerializer.Deserialize<string[]>(json),
        _ => null
    };

    public static async Task<int?> ScalarInt32Async(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<Guid?> ScalarGuidAsync(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch { Guid id => id, string text when Guid.TryParse(text, out var id) => id, _ => null };
    }

    public static async Task<bool?> ScalarNullableBoolAsync(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<DateTime?> ScalarInstantAsync(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            DateTime instant => DateTime.SpecifyKind(instant, DateTimeKind.Utc),
            string text => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).UtcDateTime,
            _ => null
        };
    }

    public static async Task<bool> ScalarBoolAsync(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool IsUniqueViolation(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => true,
        SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 or 1555 } => true,
        _ => false
    };
}
