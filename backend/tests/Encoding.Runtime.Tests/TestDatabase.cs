using System.Data.Common;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Npgsql;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Base isolée d'un scénario, sur le fournisseur choisi par ENCODING_TEST_PROVIDER (« postgres » par défaut, ou « sqlite »).
/// PostgreSQL : un schéma par scénario ; SQLite : un fichier temporaire (architecture 0043).
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public static bool IsSqlite => string.Equals(Environment.GetEnvironmentVariable("ENCODING_TEST_PROVIDER"), "sqlite", StringComparison.OrdinalIgnoreCase);
    public static string? ConfiguredConnection => IsSqlite ? "sqlite" : Environment.GetEnvironmentVariable("ENCODING_TEST_DATABASE");

    private readonly string? postgresAdmin;
    private readonly string? schema;
    private readonly string? file;
    private readonly List<DbDataSource> opened = [];

    /// <summary>Chaîne de connexion « Cluster » de la base isolée.</summary>
    public string ConnectionString { get; }
    public string Provider => IsSqlite ? DatabaseProvider.SqliteName : DatabaseProvider.Postgres;
    public DbDataSource Db { get; }

    private TestDatabase(string connectionString, DbDataSource db, string? postgresAdmin, string? schema, string? file)
    { ConnectionString = connectionString; Db = db; this.postgresAdmin = postgresAdmin; this.schema = schema; this.file = file; }

    public static async Task<TestDatabase> CreateAsync(string? postgresConnection, string prefix)
    {
        if (IsSqlite) return await CreateSqliteAsync(prefix);
        return await CreatePostgresAsync(postgresConnection, prefix);
    }

    public static async Task<TestDatabase> CreatePostgresAsync(string? postgresConnection, string prefix)
    {
        var name = prefix + "_" + Guid.NewGuid().ToString("N");
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnection);
        await using (var admin = NpgsqlDataSource.Create(postgresConnection)) await admin.CreateCommand($"CREATE SCHEMA {name}").ExecuteNonQueryAsync();
        var cluster = new NpgsqlConnectionStringBuilder(postgresConnection) { SearchPath = name }.ConnectionString;
        return new(cluster, NpgsqlDataSource.Create(cluster), postgresConnection, name, null);
    }

    public static Task<TestDatabase> CreateSqliteAsync(string prefix)
    {
        var name = prefix + "_" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), name + ".db");
        var connection = $"Data Source={path};Pooling=False";
        return Task.FromResult(new TestDatabase(connection, new SqliteDataSource(connection), null, null, path));
    }

    /// <summary>Second accès à la même base, comme un second serveur du cluster.</summary>
    public DbDataSource OpenAnother()
    {
        DbDataSource source = IsSqlite ? new SqliteDataSource(ConnectionString) : NpgsqlDataSource.Create(ConnectionString);
        opened.Add(source);
        return source;
    }

    /// <summary>Réglages à ajouter à la configuration d'une application de test.</summary>
    public IEnumerable<KeyValuePair<string, string>> Settings() =>
        [new("ConnectionStrings:Cluster", ConnectionString), new("Database:Provider", Provider)];

    public async ValueTask DisposeAsync()
    {
        foreach (var source in opened) await source.DisposeAsync();
        await Db.DisposeAsync();
        if (postgresAdmin is not null)
        {
            await using var admin = NpgsqlDataSource.Create(postgresAdmin);
            await admin.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE").ExecuteNonQueryAsync();
        }
        if (file is not null)
            foreach (var path in new[] { file, file + "-wal", file + "-shm" }) if (File.Exists(path)) File.Delete(path);
    }
}
