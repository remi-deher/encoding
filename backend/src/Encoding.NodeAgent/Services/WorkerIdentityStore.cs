using System.Security.Cryptography;
using System.Text.Json;

namespace Encoding.NodeAgent.Services;

public sealed record WorkerIdentityState(
    Guid Id,
    string? ClusterId,
    string? ClusterName,
    string? ApiBaseUrl,
    string? NodeToken,
    bool Enrolled,
    string? PairingKey = null,
    DateTimeOffset? PairingExpiresAt = null,
    bool PairingPublished = false)
{
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClusterId)
        && Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(NodeToken);

    public bool IsEnrolled => Enrolled
        && HasCredentials;

    public bool IsPairingPending => !Enrolled && HasCredentials && PairingPublished && !string.IsNullOrWhiteSpace(PairingKey);
}

public sealed class WorkerIdentityStore(NodeAgentOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string filePath = Path.Combine(options.DataDirectory, "node-identity.json");
    private WorkerIdentityState? state;

    public async Task<WorkerIdentityState> GetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is not null) return state;
            Directory.CreateDirectory(options.DataDirectory);
            if (File.Exists(filePath))
            {
                await using var stream = File.OpenRead(filePath);
                state = await JsonSerializer.DeserializeAsync<WorkerIdentityState>(stream, JsonOptions, cancellationToken);
            }
            state ??= new WorkerIdentityState(Guid.NewGuid(), null, null, null, null, false);
            if (!File.Exists(filePath)) await SaveAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkerIdentityState> SaveEnrollmentAsync(
        string clusterId,
        string clusterName,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            if (state is null && File.Exists(filePath))
            {
                await using var stream = File.OpenRead(filePath);
                state = await JsonSerializer.DeserializeAsync<WorkerIdentityState>(stream, JsonOptions, cancellationToken);
            }
            var token = state?.NodeToken ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            state = new WorkerIdentityState(state?.Id ?? Guid.NewGuid(), clusterId, clusterName, apiBaseUrl, token, false);
            await SaveAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkerIdentityState> SavePairingAsync(
        string clusterId,
        string clusterName,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            if (state is null && File.Exists(filePath))
            {
                await using var stream = File.OpenRead(filePath);
                state = await JsonSerializer.DeserializeAsync<WorkerIdentityState>(stream, JsonOptions, cancellationToken);
            }
            if (state?.IsEnrolled == true)
                throw new InvalidOperationException("Ce worker est déjà rattaché à un cluster.");

            var token = state?.NodeToken ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var canReuseKey = state?.ClusterId == clusterId
                && state.PairingExpiresAt > DateTimeOffset.UtcNow
                && !string.IsNullOrWhiteSpace(state.PairingKey);
            var pairingKey = canReuseKey ? state!.PairingKey : Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            state = new WorkerIdentityState(state?.Id ?? Guid.NewGuid(), clusterId, clusterName, apiBaseUrl, token, false, pairingKey, expiresAt);
            await SaveAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkPairingPublishedAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is null || string.IsNullOrWhiteSpace(state.PairingKey))
                throw new InvalidOperationException("La clé de jumelage locale est absente.");
            state = state with { PairingPublished = true };
            await SaveAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkEnrolledAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is null) throw new InvalidOperationException("L’identité locale du worker est absente.");
            state = state with { Enrolled = true, PairingKey = null, PairingExpiresAt = null, PairingPublished = false };
            await SaveAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveAsync(WorkerIdentityState value, CancellationToken cancellationToken)
    {
        var temporaryPath = filePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporaryPath, filePath, overwrite: true);
    }
}
