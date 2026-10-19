using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Tools;

/// <summary>Encrypted application checkpoints in the existing replicated register journal. Never stores CLI credentials.</summary>
public sealed class AiClusterStore(DbDataSource database, ClusterReconciliationRepository journal,
    ClusterIdentityRepository cluster, ClusterDataKeyStore keys, ClusterPeerAdmissionRepository? admissions = null)
{
    public async Task<string> ProtectAsync<T>(T value, string purpose, CancellationToken ct)
    {
        var identity = await cluster.GetAsync(ct);
        if (!keys.HasKey(identity.Id) && identity.Standalone && admissions is not null)
            await admissions.EnsureRootAdmissionAsync(identity.Id, ct);
        var key = keys.GetRequired(identity.Id);
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(value);
            if (plain.Length > 512 * 1024) throw new ArgumentException("Le contexte IA dépasse la limite de 512 Kio. Ouvrez une nouvelle session.");
            var nonce = RandomNumberGenerator.GetBytes(12);
            var encrypted = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, encrypted, tag, System.Text.Encoding.UTF8.GetBytes("Encoding.Ai.v1/" + identity.Id + "/" + purpose));
            return Convert.ToBase64String([.. nonce, .. tag, .. encrypted]);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public async Task<T> UnprotectAsync<T>(string encoded, string purpose, CancellationToken ct)
    {
        var identity = await cluster.GetAsync(ct);
        var key = keys.GetRequired(identity.Id);
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length < 28 || bytes.Length > 1024 * 1024) throw new ArgumentException("Message IA invalide.");
            var plain = new byte[bytes.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain,
                System.Text.Encoding.UTF8.GetBytes("Encoding.Ai.v1/" + identity.Id + "/" + purpose));
            return JsonSerializer.Deserialize<T>(plain) ?? throw new ArgumentException("Message IA vide.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public async Task SaveAsync<T>(string key, T value, CancellationToken ct) =>
        await journal.SetRegisterAsync("ai/" + key, JsonSerializer.Serialize(await ProtectAsync(value, key, ct)), ct);
    public async Task<T?> ReadAsync<T>(string key, CancellationToken ct)
    {
        await using var command = database.CreateCommand("SELECT value FROM cluster_sync_registers WHERE register_key=@key");
        command.Parameters.AddWithValue("key", "ai/" + key);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : await UnprotectAsync<T>(JsonSerializer.Deserialize<string>((string)value)!, key, ct);
    }

    public async Task<IReadOnlyList<T>> ReadPrefixAsync<T>(string prefix, CancellationToken ct)
    {
        await using var command = database.CreateCommand("SELECT register_key,value FROM cluster_sync_registers WHERE register_key LIKE @prefix ORDER BY register_key LIMIT 1000");
        command.Parameters.AddWithValue("prefix", "ai/" + prefix + "%");
        var values = new List<(string Key, string Encoded)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) values.Add((reader.GetString(0), reader.GetString(1)));
        }

        var result = new List<T>(values.Count);
        foreach (var (key, value) in values)
        {
            var purpose = key["ai/".Length..];
            var encoded = JsonSerializer.Deserialize<string>(value)!;
            result.Add(await UnprotectAsync<T>(encoded, purpose, ct));
        }
        return result;
    }
}
