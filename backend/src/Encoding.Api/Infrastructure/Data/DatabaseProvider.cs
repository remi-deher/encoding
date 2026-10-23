using System.Data.Common;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Encoding.Api.Infrastructure.Data;

/// <summary>SQLite est l'unique fournisseur de données de l'application.</summary>
public static class DatabaseProvider
{
    public const string SqliteName = "sqlite";

    public static DbDataSource CreateRuntime(IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"]?.Trim();
        var connection = configuration.GetConnectionString("Cluster") ?? "Data Source=./data/cluster.db";
        if (provider is not null && !string.Equals(provider, SqliteName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Seul le fournisseur SQLite est pris en charge. Configurez Database:Provider=sqlite.");
        try { _ = new SqliteConnectionStringBuilder(connection); }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("La connexion doit être une chaîne SQLite valide, par exemple Data Source=./data/cluster.db.", exception);
        }
        var writer = configuration.GetSection("Database:Writer").Get<SqliteWriterOptions>() ?? new();
        // Raft retains its existing coordinator/capture path until it has its own integration tests.
        // In particular it must not wait for a projection while holding a second local writer gate.
        if (configuration.GetValue("PeerCluster:Enabled", false)) writer = writer with { Enabled = false };
        return new SqliteDataSource(connection, writer);
    }
}
