using Npgsql;

namespace Encoding.Api.Infrastructure;

public static class DatabaseMigrator
{
    public static async Task ApplyAsync(NpgsqlDataSource dataSource, string contentRoot, CancellationToken cancellationToken)
    {
        var outputMigrationDirectory = Path.Combine(AppContext.BaseDirectory, "migrations");
        var migrationDirectory = Directory.Exists(outputMigrationDirectory)
            ? outputMigrationDirectory
            : Path.Combine(contentRoot, "migrations");
        if (!Directory.Exists(migrationDirectory))
            throw new DirectoryNotFoundException($"Dossier de migrations introuvable : {migrationDirectory}");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext('encoding.schema_migrations'))", connection))
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
            await command.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            foreach (var file in Directory.GetFiles(migrationDirectory, "*.sql").Order(StringComparer.Ordinal))
            {
                var version = Path.GetFileNameWithoutExtension(file);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using (var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = @version)", connection, transaction))
                {
                    check.Parameters.AddWithValue("version", version);
                    if ((bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false))
                    {
                        await transaction.CommitAsync(cancellationToken);
                        continue;
                    }
                }

                var sql = await File.ReadAllTextAsync(file, cancellationToken);
                await using (var apply = new NpgsqlCommand(sql, connection, transaction))
                    await apply.ExecuteNonQueryAsync(cancellationToken);
                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES (@version)", connection, transaction))
                {
                    record.Parameters.AddWithValue("version", version);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('encoding.schema_migrations'))", connection);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
