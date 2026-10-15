using Encoding.Api.Infrastructure.Data;
using System.Data.Common;

namespace Encoding.Api.Infrastructure;

/// <summary>État des migrations vu depuis la base : appliquées, en attente, et appliquées mais absentes de ce binaire.</summary>
public sealed record MigrationStatus(int Applied, IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown, string? Latest);

public static class DatabaseMigrator
{
    private const string LockSql = "SELECT pg_advisory_lock(hashtext('encoding.schema_migrations'))";

    public static async Task ApplyAsync(DbDataSource dataSource, string contentRoot, CancellationToken cancellationToken, ILogger? logger = null)
    {
        var sqlite = Sql.IsSqlite(dataSource);
        var files = MigrationCatalog.Load(MigrationCatalog.ResolveDirectory(contentRoot, sqlite));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var lockCommand = Sql.Command(LockSql, connection))
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            if (sqlite)
            {
                await using var create = Sql.Command("CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, applied_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')), checksum TEXT)", connection);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using (var command = Sql.Command("CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
                    await command.ExecuteNonQueryAsync(cancellationToken);
                await using (var command = Sql.Command("ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS checksum text", connection))
                    await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var applied = await ReadAppliedAsync(connection, cancellationToken);
            foreach (var file in files)
            {
                if (applied.TryGetValue(file.Version, out var recorded))
                {
                    if (recorded is null) await RecordAsync(connection, null, file, backfill: true, cancellationToken);
                    else if (recorded != file.Checksum)
                        throw new InvalidOperationException($"La migration {file.Version} a été modifiée après son application. Créer une nouvelle migration au lieu de modifier une migration publiée.");
                    continue;
                }
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                if (MigrationCatalog.RenamedFrom.TryGetValue(file.Version, out var legacy) && applied.ContainsKey(legacy))
                {
                    await RecordAsync(connection, transaction, file, backfill: false, cancellationToken);
                    logger?.LogInformation("Migration {Version} déjà appliquée sous l’ancien nom {Legacy}.", file.Version, legacy);
                }
                else
                {
                    var sql = await File.ReadAllTextAsync(file.Path, cancellationToken);
                    await using (var apply = Sql.Command(sql, connection, transaction))
                        await apply.ExecuteNonQueryAsync(cancellationToken);
                    await RecordAsync(connection, transaction, file, backfill: false, cancellationToken);
                    logger?.LogInformation("Migration {Version} appliquée.", file.Version);
                }
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            await using var unlockCommand = Sql.Command("SELECT pg_advisory_unlock(hashtext('encoding.schema_migrations'))", connection);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>Lecture seule : ne crée ni ne modifie rien, pour le health check et les métriques.</summary>
    public static async Task<MigrationStatus> StatusAsync(DbDataSource dataSource, string contentRoot, CancellationToken cancellationToken)
    {
        var files = MigrationCatalog.Load(MigrationCatalog.ResolveDirectory(contentRoot, Sql.IsSqlite(dataSource)));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        Dictionary<string, string?> applied;
        await using (var exists = Sql.Command("SELECT to_regclass('schema_migrations') IS NOT NULL", connection))
            applied = Convert.ToBoolean(await exists.ExecuteScalarAsync(cancellationToken))
                ? await ReadAppliedAsync(connection, cancellationToken)
                : [];
        var known = files.Select(file => file.Version).ToHashSet(StringComparer.Ordinal);
        var legacy = MigrationCatalog.RenamedFrom.Values.ToHashSet(StringComparer.Ordinal);
        var pending = files.Where(file => !applied.ContainsKey(file.Version)
            && !(MigrationCatalog.RenamedFrom.TryGetValue(file.Version, out var old) && applied.ContainsKey(old))).Select(file => file.Version).ToList();
        var unknown = applied.Keys.Where(version => !known.Contains(version) && !legacy.Contains(version)).Order(StringComparer.Ordinal).ToList();
        return new(applied.Count, pending, unknown, applied.Keys.Order(StringComparer.Ordinal).LastOrDefault());
    }

    private static async Task<Dictionary<string, string?>> ReadAppliedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var command = Sql.Command("SELECT version, checksum FROM schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return result;
    }

    private static async Task RecordAsync(DbConnection connection, DbTransaction? transaction, MigrationFile file, bool backfill, CancellationToken cancellationToken)
    {
        var sql = backfill
            ? "UPDATE schema_migrations SET checksum = @checksum WHERE version = @version AND checksum IS NULL"
            : "INSERT INTO schema_migrations(version, checksum) VALUES (@version, @checksum)";
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue("version", file.Version);
        command.Parameters.AddWithValue("checksum", file.Checksum);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
