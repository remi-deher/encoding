using System.Data.Common;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Npgsql;

namespace Encoding.Api.Infrastructure.Data;

/// <summary>
/// Choix du fournisseur de base de données (architecture 0043). <c>Database:Provider</c> vaut « postgres » (défaut) ou « sqlite » ;
/// SQLite est réservé aux clusters à un seul serveur. La chaîne de connexion « Cluster » est celle du fournisseur choisi.
/// </summary>
public static class DatabaseProvider
{
    public const string Postgres = "postgres";
    public const string SqliteName = "sqlite";

    /// <summary>
    /// Fournisseur choisi : <c>Database:Provider</c> s'il est fourni ; sinon déduit de la chaîne de connexion (« Data Source=… » sans « Host= »
    /// désigne SQLite), ce qui laisse les installations PostgreSQL existantes inchangées.
    /// </summary>
    public static string Name(IConfiguration configuration) =>
        (configuration["Database:Provider"] ?? Infer(configuration.GetConnectionString("Cluster"))).Trim().ToLowerInvariant() switch
        {
            Postgres or "postgresql" => Postgres,
            SqliteName => SqliteName,
            var other => throw new InvalidOperationException($"Fournisseur de base de données inconnu : « {other} ». Valeurs possibles : postgres, sqlite.")
        };

    private static string Infer(string? connectionString) =>
        connectionString is not null
        && connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
        && !connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            ? SqliteName : Postgres;

    public static DbDataSource Create(IConfiguration configuration, string connectionString) => Name(configuration) switch
    {
        SqliteName => new SqliteDataSource(connectionString),
        _ => NpgsqlDataSource.Create(connectionString)
    };
}
