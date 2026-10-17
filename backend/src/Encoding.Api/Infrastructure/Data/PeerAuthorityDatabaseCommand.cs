using Encoding.Api.Infrastructure.Data.Sqlite;

namespace Encoding.Api.Infrastructure.Data;

/// <summary>
/// Initializes a new peer's local database from the one explicitly selected authority database.
/// This is a one-way copy into an empty target, never a merge.
/// </summary>
public static class PeerAuthorityDatabaseCommand
{
    public const string Name = "initialize-peer-database";

    public static async Task<int> RunAsync(string[] args, string contentRoot, TextWriter output, CancellationToken ct)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2) options[args[i].TrimStart('-')] = args[i + 1];
        foreach (var required in new[] { "authority", "target" })
            if (!options.ContainsKey(required))
            {
                await output.WriteLineAsync($"Option manquante : --{required}. Usage : {Name} --authority <connexion SQLite> --target <connexion SQLite>");
                return 2;
            }

        if (string.Equals(Canonicalize(options["authority"]), Canonicalize(options["target"]), StringComparison.Ordinal))
        {
            await output.WriteLineAsync("La base faisant autorité et la cible doivent être deux bases distinctes.");
            return 2;
        }

        await using var authority = Open(options["authority"]);
        await using var target = Open(options["target"]);
        var pending = await DatabaseMigrator.StatusAsync(authority, contentRoot, ct);
        if (pending.Pending.Count > 0 || pending.Unknown.Count > 0)
        {
            await output.WriteLineAsync($"La base faisant autorité a {pending.Pending.Count} migration(s) en attente : démarrez-la une fois avec sa version actuelle avant l’initialisation des pairs.");
            return 3;
        }

        // The copy helper rejects any populated application table and performs the data copy in one transaction.
        await DatabaseMigrator.ApplyAsync(target, contentRoot, ct);
        var copied = await DatabaseTransfer.CopyAsync(authority, target, ct);
        var authorityCounts = await DatabaseTransfer.CountsAsync(authority, ct);
        var targetCounts = await DatabaseTransfer.CountsAsync(target, ct);
        var mismatches = authorityCounts.Keys.Union(targetCounts.Keys, StringComparer.Ordinal)
            .Where(table => !authorityCounts.GetValueOrDefault(table).Equals(targetCounts.GetValueOrDefault(table)))
            .Order(StringComparer.Ordinal).ToArray();
        if (mismatches.Length > 0)
            throw new InvalidDataException("La vérification après copie échoue pour : " + string.Join(", ", mismatches));

        foreach (var (table, rows) in copied.Rows.Where(pair => pair.Value > 0))
            await output.WriteLineAsync($"{table}: {rows}");
        await output.WriteLineAsync($"Pair initialisé depuis la base faisant autorité : {copied.Total} ligne(s) copiée(s), compteurs vérifiés. Aucune fusion effectuée.");
        return 0;
    }

    private static string Canonicalize(string connection)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection);
        if (builder.DataSource != ":memory:" && !builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            builder.DataSource = Path.GetFullPath(builder.DataSource);
        return builder.ToString();
    }

    private static SqliteDataSource Open(string connection) => new(connection);
}
