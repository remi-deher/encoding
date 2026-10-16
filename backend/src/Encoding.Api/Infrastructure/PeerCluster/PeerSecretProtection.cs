using System.Data.Common;
using System.Security.Cryptography;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Encoding.Api.Infrastructure.PeerCluster;

/// <summary>A shared deployment key protects replicated plugin secrets independently of local session/key rings.</summary>
public sealed class PeerSecretProtection(PeerClusterOptions options, IDataProtectionProvider legacy)
{
    private readonly byte[] key = Convert.FromBase64String(options.SharedDataKey);
    private readonly IDataProtector previous = legacy.CreateProtector("Encoding.PluginConfiguration.v1");
    public string KeyId => Convert.ToHexStringLower(SHA256.HashData(key));
    private static ReadOnlySpan<byte> Magic => "EPC1"u8;
    public byte[] Protect(byte[] plain)
    {
        var result = new byte[4 + 12 + 16 + plain.Length];
        Magic.CopyTo(result);
        RandomNumberGenerator.Fill(result.AsSpan(4, 12));
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(result.AsSpan(4, 12), plain, result.AsSpan(32), result.AsSpan(16, 16), Magic);
        return result;
    }
    public byte[] Unprotect(byte[] encrypted)
    {
        if (!encrypted.AsSpan().StartsWith(Magic)) return previous.Unprotect(encrypted);
        if (encrypted.Length < 32) throw new CryptographicException("Paramètres chiffrés tronqués.");
        var result = new byte[encrypted.Length - 32];
        using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(encrypted.AsSpan(4, 12), encrypted.AsSpan(32), encrypted.AsSpan(16, 16), result, Magic);
        return result;
    }
    public async Task RewrapAsync(DbConnection connection, DbTransaction tx, CancellationToken ct)
    {
        foreach (var (table, column, idColumns) in new[]
        {
            ("plugin_configurations", "encrypted_values", new[] { "plugin_id" }),
            ("flow_plugin_configurations", "encrypted_values", new[] { "flow_id", "plugin_id" }),
            ("execution_work", "plugin_configuration_snapshot", new[] { "execution_id" })
        })
        {
            var records = new List<(object[] Ids, byte[] Value)>();
            await using (var read = Sql.Command($"SELECT {string.Join(",", idColumns)},{column} FROM {table} WHERE {column} IS NOT NULL", connection, tx))
            await using (var rows = await read.ExecuteReaderAsync(ct))
                while (await rows.ReadAsync(ct))
                {
                    var value = (byte[])rows[idColumns.Length];
                    if (!value.AsSpan().StartsWith(Magic))
                        records.Add((Enumerable.Range(0, idColumns.Length).Select(rows.GetValue).ToArray(), Protect(previous.Unprotect(value))));
                }
            foreach (var row in records)
            {
                await using var update = Sql.Command($"UPDATE {table} SET {column}=@value WHERE {string.Join(" AND ", idColumns.Select(id => id + "=@" + id))}", connection, tx);
                update.Parameters.AddWithValue("value", SqlKind.Bytes, row.Value);
                for (var i = 0; i < idColumns.Length; i++) update.Parameters.AddWithValue(idColumns[i], row.Ids[i]);
                await update.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
