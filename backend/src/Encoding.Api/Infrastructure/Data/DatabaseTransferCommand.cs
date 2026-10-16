using Microsoft.Extensions.Configuration;

namespace Encoding.Api.Infrastructure.Data;

/// <summary>
/// Commande d'export/import entre fournisseurs (architecture 0043) :
/// <c>Encoding.Api transfer-database --from-provider sqlite --from "Data Source=…" --to-provider postgres --to "Host=…"</c>.
/// Le serveur doit être arrêté ; la source n'est jamais modifiée et la cible doit être vide (elle est migrée si besoin).
/// </summary>
public static class DatabaseTransferCommand
{
    public const string Name = "transfer-database";

    public static async Task<int> RunAsync(string[] args, string contentRoot, TextWriter output, CancellationToken ct)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2) options[args[i].TrimStart('-')] = args[i + 1];
        foreach (var required in new[] { "from-provider", "from", "to-provider", "to" })
            if (!options.ContainsKey(required))
            {
                await output.WriteLineAsync($"Option manquante : --{required}. Usage : {Name} --from-provider sqlite|postgres --from <connexion> --to-provider sqlite|postgres --to <connexion>");
                return 2;
            }
        await using var source = Open(options["from-provider"], options["from"]);
        await using var target = Open(options["to-provider"], options["to"]);
        var pending = await DatabaseMigrator.StatusAsync(source, contentRoot, ct);
        if (pending.Pending.Count > 0 || pending.Unknown.Count > 0)
        {
            await output.WriteLineAsync($"La source a {pending.Pending.Count} migration(s) en attente : démarrez le serveur une fois avec sa version actuelle avant l'export.");
            return 3;
        }
        await DatabaseMigrator.ApplyAsync(target, contentRoot, ct);
        var result = await DatabaseTransfer.CopyAsync(source, target, ct);
        foreach (var (table, rows) in result.Rows.Where(pair => pair.Value > 0)) await output.WriteLineAsync($"{table}: {rows}");
        await output.WriteLineAsync($"Terminé : {result.Total} ligne(s) copiée(s). La source n'a pas été modifiée.");
        return 0;
    }

    private static System.Data.Common.DbDataSource Open(string provider, string connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = provider }).Build();
        return DatabaseProvider.Create(configuration, connection);
    }
}
