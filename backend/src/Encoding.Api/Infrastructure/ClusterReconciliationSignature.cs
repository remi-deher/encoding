using System.Security.Cryptography;

namespace Encoding.Api.Infrastructure;

/// <summary>Authentifie le contenu de l'échange sans envoyer la clé partagée sur le réseau.</summary>
public static class ClusterReconciliationSignature
{
    public const string TimeHeader = "X-Encoding-Cluster-Reconciliation-Time";
    public const string SignatureHeader = "X-Encoding-Cluster-Reconciliation-Signature";
    public const int MaximumBodyBytes = 16 * 1024 * 1024;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public static string Create(string secret, string timestamp, ReadOnlySpan<byte> body) =>
        Convert.ToBase64String(HMACSHA256.HashData(Key(secret), SigningBytes(timestamp, body)));

    public static bool Verify(string secret, string? timestamp, string? signature, ReadOnlySpan<byte> body, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature)
            || !long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return false;
        DateTimeOffset requestTime;
        try { requestTime = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (Math.Abs(((now ?? DateTimeOffset.UtcNow) - requestTime).TotalSeconds) > MaximumClockSkew.TotalSeconds) return false;
        byte[] provided;
        try { provided = Convert.FromBase64String(signature); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Key(secret), SigningBytes(timestamp, body));
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static byte[] Key(string secret) => System.Text.Encoding.UTF8.GetBytes(secret);

    private static byte[] SigningBytes(string timestamp, ReadOnlySpan<byte> body)
    {
        var time = System.Text.Encoding.UTF8.GetBytes(timestamp);
        var result = new byte[time.Length + 1 + body.Length];
        time.CopyTo(result, 0);
        result[time.Length] = (byte)'\n';
        body.CopyTo(result.AsSpan(time.Length + 1));
        return result;
    }
}
