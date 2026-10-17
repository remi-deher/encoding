using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure.PeerCluster;
using Encoding.Api.Infrastructure.Data.Sqlite;

namespace Encoding.Api.Infrastructure.Data;

public sealed record PeerBackupManifest(int Format, Guid BackupSet, Guid PeerId, Guid ClusterId, DateTimeOffset CreatedAtUtc,
    IReadOnlyDictionary<string, string> Sha256, IReadOnlyDictionary<string, long> Rows);

/// <summary>Cold backup/restore of one peer. Its identity lock makes running the command against a live peer fail.</summary>
public static class PeerOperationsCommand
{
    public static readonly string[] Names = ["backup-peer", "restore-peer", "activate-peer-certificate"];
    public static async Task<int> RunAsync(string name, string[] args, string contentRoot, TextWriter output, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length % 2 != 0) throw new ArgumentException("Chaque option doit avoir une valeur.");
        for (var i = 0; i < args.Length; i += 2)
            if (!values.TryAdd(args[i].TrimStart('-'), args[i + 1])) throw new ArgumentException("Option répétée.");
        string Required(string option) => values.GetValueOrDefault(option) ?? throw new ArgumentException($"Option requise : --{option}");
        var directory = Path.GetFullPath(Required("data-directory"));
        var clusterId = Guid.Parse(Required("cluster-id"));
        var password = Environment.GetEnvironmentVariable("PeerCluster__IdentityPassword")
            ?? throw new InvalidOperationException("PeerCluster__IdentityPassword est requis dans l’environnement.");
        if (name == "activate-peer-certificate")
        {
            using var identity = PeerIdentityStore.Open(directory, clusterId, pfxPassword: password);
            identity.ActivateRotation(password, Required("approved-fingerprint"));
            await output.WriteLineAsync("Certificat activé. Redémarrez ce pair avec son volume et sa membership existants.");
            return 0;
        }
        var connectionString = Required("database");
        connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString();
        await using var database = new SqliteDataSource(connectionString);
        var keys = Path.GetFullPath(Required("key-directory"));
        if (Within(directory, keys) || Within(keys, directory))
            throw new ArgumentException("Les dossiers d’identité et de clés doivent être distincts et non imbriqués.");
        if (name == "backup-peer")
        {
            if (!File.Exists(Path.Combine(directory, "peer-identity.json"))) throw new InvalidOperationException("Identité persistée absente.");
            var destination = Path.GetFullPath(Required("output"));
            if (Within(destination, directory) || Within(destination, keys))
                throw new ArgumentException("La sauvegarde doit être hors des volumes du pair et des clés.");
            EnsureEmpty(destination);
            using var identity = PeerIdentityStore.Open(directory, clusterId, pfxPassword: password);
            Directory.CreateDirectory(destination);
            PrivateDirectory(destination);
            var backupDatabase = Path.Combine(destination, "database.sqlite");
            await using (var target = new Sqlite.SqliteDataSource("Data Source=" + backupDatabase + ";Pooling=False"))
            {
                var status = await DatabaseMigrator.StatusAsync(database, contentRoot, ct);
                if (status.Pending.Count != 0 || status.Unknown.Count != 0) throw new InvalidOperationException("La version de schéma doit correspondre au binaire.");
                await DatabaseMigrator.ApplyAsync(target, contentRoot, ct);
                await DatabaseTransfer.CopyAsync(database, target, ct);
                await using var checkpoint = target.CreateCommand("PRAGMA wal_checkpoint(TRUNCATE)");
                await checkpoint.ExecuteNonQueryAsync(ct);
            }
            CopyDirectory(directory, Path.Combine(destination, "peer"));
            CopyDirectory(keys, Path.Combine(destination, "keys"));
            var files = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith("-wal", StringComparison.Ordinal) && !file.EndsWith("-shm", StringComparison.Ordinal)).ToArray();
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                PeerIdentityStore.SetPrivateFilePermissions(file);
                await using var stream = File.OpenRead(file);
                hashes.Add(Path.GetRelativePath(destination, file), Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)));
            }
            var manifest = new PeerBackupManifest(1, Guid.Parse(Required("backup-set")), identity.Identity.PeerId, clusterId,
                DateTimeOffset.UtcNow, hashes, await DatabaseTransfer.CountsAsync(database, ct));
            var manifestPath = Path.Combine(destination, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), ct);
            PeerIdentityStore.SetPrivateFilePermissions(manifestPath);
            await output.WriteLineAsync($"Sauvegarde complète du pair {identity.Identity.PeerId:D}, ensemble {manifest.BackupSet:D}.");
        }
        else if (name == "restore-peer")
        {
            var backup = Path.GetFullPath(Required("input"));
            if (Within(directory, backup) || Within(keys, backup))
                throw new ArgumentException("Les cibles de restauration doivent être hors de l’archive.");
            var manifest = JsonSerializer.Deserialize<PeerBackupManifest>(await File.ReadAllTextAsync(Path.Combine(backup, "manifest.json"), ct))
                ?? throw new InvalidDataException("Manifeste absent.");
            if (manifest.Format != 1 || manifest.ClusterId != clusterId || manifest.BackupSet != Guid.Parse(Required("backup-set")))
                throw new InvalidDataException("Format, cluster ou ensemble de sauvegarde incompatible.");
            foreach (var (relative, hash) in manifest.Sha256)
            {
                var file = Path.GetFullPath(Path.Combine(backup, relative));
                if (!file.StartsWith(backup + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("Chemin de sauvegarde invalide.");
                await using var stream = File.OpenRead(file);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != hash)
                    throw new InvalidDataException("Une empreinte de sauvegarde est incorrecte.");
            }
            foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Lien dans une sauvegarde.");
                var relative = Path.GetRelativePath(backup, file);
                if (relative != "manifest.json" && !relative.EndsWith(".lock", StringComparison.Ordinal)
                    && !relative.EndsWith("-wal", StringComparison.Ordinal) && !relative.EndsWith("-shm", StringComparison.Ordinal)
                    && !manifest.Sha256.ContainsKey(relative))
                    throw new InvalidDataException("Un fichier ne figure pas dans le manifeste.");
            }
            using (var identity = PeerIdentityStore.Open(Path.Combine(backup, "peer"), clusterId, pfxPassword: password))
                if (identity.Identity.PeerId != manifest.PeerId) throw new InvalidDataException("L’identité de la sauvegarde ne correspond pas au manifeste.");
            EnsureEmpty(directory);
            EnsureEmpty(keys);
            Directory.CreateDirectory(directory);
            PrivateDirectory(directory);
            await using var restoreLock = new FileStream(Path.Combine(directory, "peer-identity.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await using (var source = new Sqlite.SqliteDataSource("Data Source=" + Path.Combine(backup, "database.sqlite") + ";Pooling=False"))
            {
                await DatabaseMigrator.ApplyAsync(database, contentRoot, ct);
                await DatabaseTransfer.CopyAsync(source, database, ct);
            }
            var counts = await DatabaseTransfer.CountsAsync(database, ct);
            if (manifest.Rows.Any(row => counts.GetValueOrDefault(row.Key) != row.Value))
                throw new InvalidDataException("La vérification des lignes restaurées a échoué.");
            CopyDirectory(Path.Combine(backup, "peer"), directory);
            CopyDirectory(Path.Combine(backup, "keys"), keys);
            await output.WriteLineAsync($"Pair {manifest.PeerId:D} restauré. Conservez son endpoint et sa clé de protection partagée. La copie originale doit être arrêtée.");
        }
        else throw new ArgumentException("Commande inconnue.");
        return 0;
    }
    private static bool Within(string candidate, string parent) => candidate == parent
        || candidate.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void EnsureEmpty(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("Le dossier cible doit être vide.");
    }
    private static void PrivateDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Lien de dossier non accepté dans une sauvegarde.");
        Directory.CreateDirectory(target);
        PrivateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (file.EndsWith(".lock", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Lien de fichier non accepté dans une sauvegarde.");
            var destination = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: false);
            PeerIdentityStore.SetPrivateFilePermissions(destination);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
