using System.Data.Common;
using System.Globalization;

namespace Encoding.Api.Infrastructure.Data;

public sealed record DatabaseTransferResult(IReadOnlyDictionary<string, long> Rows)
{
    public long Total => Rows.Values.Sum();
}

/// <summary>
/// Export/import entre fournisseurs (architecture 0043) : copie toutes les données d'une base migrée vers une base cible migrée et vide,
/// dans une seule transaction, dans l'ordre des clés étrangères. Le changement de mode est donc toujours explicite : la source n'est pas modifiée.
/// </summary>
public static class DatabaseTransfer
{
    // Tables de gestion propres à chaque fournisseur ; l'identité du cluster est remplacée par celle de la source.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "schema_migrations" };
    private static readonly HashSet<string> Replaced = new(StringComparer.Ordinal) { "cluster_identity" };

    public static async Task<DatabaseTransferResult> CopyAsync(DbDataSource source, DbDataSource target, CancellationToken ct)
    {
        var sourceTables = (await TablesAsync(source, ct)).Except(Skipped).ToHashSet(StringComparer.Ordinal);
        var targetTables = (await TablesAsync(target, ct)).Except(Skipped).ToHashSet(StringComparer.Ordinal);
        var missing = sourceTables.Except(targetTables).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("La cible n'a pas ces tables de la source : " + string.Join(", ", missing.Order()) + ". Migrez-la à la même version.");
        var order = await DependencyOrderAsync(target, sourceTables, ct);

        await using var read = await source.OpenConnectionAsync(ct);
        await using var write = await target.OpenConnectionAsync(ct);
        await using var readTransaction = await read.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var transaction = await write.BeginTransactionAsync(ct);
        foreach (var table in order)
        {
            var sourceColumns = await ColumnsAsync(read, readTransaction, table, ct);
            var targetColumns = await ColumnsAsync(write, transaction, table, ct);
            if (!sourceColumns.Select(column => column.Name).Order().SequenceEqual(targetColumns.Select(column => column.Name).Order()))
                throw new InvalidOperationException($"Les colonnes de la table {table} ne correspondent pas.");
        }
        foreach (var table in order)
            if (!Replaced.Contains(table) && await CountAsync(write, transaction, table, ct) > 0)
                throw new InvalidOperationException($"La cible n'est pas vide : la table {table} contient déjà des lignes.");
        foreach (var table in Replaced.Intersect(order))
            await ExecuteAsync(write, transaction, $"DELETE FROM {table}", ct);

        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in order)
        {
            rows[table] = await CopyTableAsync(read, readTransaction, write, transaction, table, ct);
            if (rows[table] != await CountAsync(read, readTransaction, table, ct))
                throw new InvalidDataException($"La copie de {table} est incomplète.");
        }
        await transaction.CommitAsync(ct);
        await readTransaction.CommitAsync(ct);
        return new(rows);
    }

    /// <summary>Nombre de lignes par table (hors tables de gestion), pour comparer deux bases.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> CountsAsync(DbDataSource dataSource, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in (await TablesAsync(dataSource, ct)).Except(Skipped)) counts[table] = await CountAsync(connection, null, table, ct);
        return counts;
    }

    private static async Task<long> CopyTableAsync(DbConnection read, DbTransaction readTransaction, DbConnection write, DbTransaction transaction, string table, CancellationToken ct)
    {
        var columns = await ColumnsAsync(write, transaction, table, ct);
        var names = string.Join(", ", columns.Select(column => column.Name));
        // Retry chains have a self foreign key: a parent must be copied before its children.
        var selectSql = table == "executions"
            ? $"WITH RECURSIVE execution_order(id,depth) AS (SELECT id,0 FROM executions WHERE retry_of IS NULL UNION ALL SELECT e.id,o.depth+1 FROM executions e JOIN execution_order o ON e.retry_of=o.id) SELECT {string.Join(", ", columns.Select(column => "e." + column.Name))} FROM executions e JOIN execution_order o ON e.id=o.id ORDER BY o.depth,e.id"
            : $"SELECT {names} FROM {table}";
        await using var select = Sql.Command(selectSql, read, readTransaction);
        await using var reader = await select.ExecuteReaderAsync(ct);
        var sqlite = Sql.IsSqlite(write);
        // Côté PostgreSQL, la conversion explicite type aussi les valeurs nulles (le paramètre nul n'a pas de type propre).
        var insertSql = $"INSERT INTO {table}({names}) VALUES({string.Join(", ", columns.Select(column => sqlite ? "@" + column.Name : $"CAST(@{column.Name} AS {PostgresType(column.Type)})"))})";
        long count = 0;
        while (await reader.ReadAsync(ct))
        {
            await using var insert = Sql.Command(insertSql, write, transaction);
            for (var i = 0; i < columns.Count; i++) AddValue(insert, columns[i], reader.IsDBNull(i) ? null : reader.GetValue(i), sqlite);
            await insert.ExecuteNonQueryAsync(ct);
            count++;
        }
        return count;
    }

    private static void AddValue(DbCommand command, Column column, object? value, bool targetIsSqlite)
    {
        if (value is null) { command.Parameters.AddWithValue(column.Name, column.Kind ?? SqlKind.Text, DBNull.Value); return; }
        if (targetIsSqlite) { command.Parameters.AddWithValue(column.Name, value); return; }
        switch (column.Type)
        {
            case "uuid": command.Parameters.AddWithValue(column.Name, value is Guid ? value : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)); break;
            case "jsonb" or "json": command.Parameters.AddWithValue(column.Name, SqlKind.Json, Convert.ToString(value, CultureInfo.InvariantCulture)!); break;
            case "_text": command.Parameters.AddWithValue(column.Name, SqlKind.TextArray, Sql.ToStringArray(value) ?? []); break;
            case "bytea": command.Parameters.AddWithValue(column.Name, SqlKind.Bytes, (byte[])value); break;
            case "bool": command.Parameters.AddWithValue(column.Name, Convert.ToBoolean(value, CultureInfo.InvariantCulture)); break;
            case "int2": command.Parameters.AddWithValue(column.Name, Convert.ToInt16(value, CultureInfo.InvariantCulture)); break;
            case "int4": command.Parameters.AddWithValue(column.Name, Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
            case "int8": command.Parameters.AddWithValue(column.Name, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case "numeric": command.Parameters.AddWithValue(column.Name, Convert.ToDecimal(value, CultureInfo.InvariantCulture)); break;
            case "float4" or "float8": command.Parameters.AddWithValue(column.Name, Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
            case "timestamptz" or "timestamp": command.Parameters.AddWithValue(column.Name, ToInstant(value)); break;
            default: command.Parameters.AddWithValue(column.Name, SqlKind.Text, Convert.ToString(value, CultureInfo.InvariantCulture)!); break;
        }
    }

    // Les instants SQLite sont du texte UTC ; l'infini (9999-12-31) redevient l'infini PostgreSQL (DateTime.MaxValue).
    private static DateTime ToInstant(object value)
    {
        var instant = value switch
        {
            DateTime date => DateTime.SpecifyKind(date, DateTimeKind.Utc),
            DateTimeOffset offset => offset.UtcDateTime,
            _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime
        };
        return instant.Year >= 9999 ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : instant;
    }

    private static string PostgresType(string udt) => udt switch
    {
        "_text" => "text[]", "int2" => "smallint", "int4" => "integer", "int8" => "bigint", "float4" => "real", "float8" => "double precision",
        "bool" => "boolean", "timestamptz" => "timestamptz", _ => udt
    };

    private sealed record Column(string Name, string Type, SqlKind? Kind);

    private static async Task<List<Column>> ColumnsAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken ct)
    {
        var sql = Sql.IsSqlite(connection)
            ? "SELECT name, type FROM pragma_table_xinfo(@table) WHERE hidden = 0 ORDER BY cid"
            : "SELECT column_name, udt_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table AND is_generated = 'NEVER' ORDER BY ordinal_position";
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new List<Column>();
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetString(1).ToLowerInvariant();
            columns.Add(new(reader.GetString(0), type, type switch { "blob" or "bytea" => SqlKind.Bytes, "integer" or "int4" or "int8" => SqlKind.Integer, _ => null }));
        }
        return columns;
    }

    private static async Task<List<string>> TablesAsync(DbDataSource dataSource, CancellationToken ct)
    {
        var sql = Sql.IsSqlite(dataSource)
            ? "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'"
            : "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'";
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(ct))
            if (!reader.GetString(0).StartsWith("peer_replication_", StringComparison.Ordinal)) tables.Add(reader.GetString(0));
        return tables;
    }

    // Tri topologique : une table passe après celles qu'elle référence (les auto-références sont ignorées).
    private static async Task<List<string>> DependencyOrderAsync(DbDataSource target, HashSet<string> tables, CancellationToken ct)
    {
        var sql = Sql.IsSqlite(target)
            ? "SELECT m.name, f.\"table\" FROM sqlite_master m, pragma_foreign_key_list(m.name) f WHERE m.type = 'table'"
            : """
              SELECT c.relname, p.relname FROM pg_constraint k
              JOIN pg_class c ON c.oid = k.conrelid JOIN pg_class p ON p.oid = k.confrelid
              WHERE k.contype = 'f' AND c.relnamespace = current_schema()::regnamespace
              """;
        await using var command = target.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var dependencies = tables.ToDictionary(table => table, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var child = reader.GetString(0); var parent = reader.GetString(1);
            if (child != parent && dependencies.TryGetValue(child, out var set) && tables.Contains(parent)) set.Add(parent);
        }
        var ordered = new List<string>();
        var remaining = new SortedSet<string>(tables, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(table => dependencies[table].All(parent => !remaining.Contains(parent))).ToList();
            if (ready.Count == 0) throw new InvalidOperationException("Dépendance circulaire entre tables : " + string.Join(", ", remaining));
            foreach (var table in ready) { ordered.Add(table); remaining.Remove(table); }
        }
        return ordered;
    }

    private static async Task<long> CountAsync(DbConnection connection, DbTransaction? transaction, string table, CancellationToken ct)
    {
        await using var command = Sql.Command($"SELECT count(*) FROM {table}", connection, transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = Sql.Command(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }
}
