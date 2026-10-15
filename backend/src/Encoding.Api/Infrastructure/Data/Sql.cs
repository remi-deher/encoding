using System.Data.Common;
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

    public static DbParameter Parameter(string name, SqlKind kind, object? value)
    {
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
        var parameter = new NpgsqlParameter { ParameterName = name, Value = value ?? DBNull.Value };
        parameters.Add(parameter);
        return parameter;
    }

    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, SqlKind kind, object? value)
    {
        var parameter = Parameter(name, kind, value);
        parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Violation d'unicité signalée par le fournisseur.</summary>
    public static bool IsUniqueViolation(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
