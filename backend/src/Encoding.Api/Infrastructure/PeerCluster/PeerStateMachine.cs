using System.Text.Json;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerReplicatedStateCommand(Guid CommandId, string Key, JsonElement Value);
public sealed record PeerReplicatedStateSnapshot(Dictionary<string, JsonElement> Values, Guid[] AppliedCommandIds);

/// <summary>Durable replicated key/value projection used by peer-cluster commands.</summary>
public sealed class PeerStateMachine : SimpleStateMachine
{
    public const int MaximumCommandBytes = 1_048_576;
    private readonly object sync = new();
    private readonly Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> appliedCommands = [];

    public PeerStateMachine(PeerClusterOptions options)
        : base(new DirectoryInfo(Path.Combine(options.DataDirectory, "raft", "snapshots")))
    {
    }

    public JsonElement? Get(string key)
    {
        lock (sync) return values.TryGetValue(key, out var value) ? value.Clone() : null;
    }

    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken cancellationToken)
    {
        if (entry.IsConfiguration) return false;
        // Raft uses zero-length entries as heartbeats/barriers during membership changes.
        if (entry.Length == 0) return false;
        var bytes = await entry.ToByteArrayAsync(token: cancellationToken);
        if (bytes.Length > MaximumCommandBytes) throw new InvalidDataException("Entrée de réplication trop volumineuse.");
        var command = JsonSerializer.Deserialize<PeerReplicatedStateCommand>(bytes)
            ?? throw new InvalidDataException("Commande de réplication invalide.");
        if (command.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(command.Key) || command.Key.Length > 256)
            throw new InvalidDataException("Identifiant ou clé de commande invalide.");
        lock (sync)
        {
            if (!appliedCommands.Add(command.CommandId)) return false;
            values[command.Key] = command.Value.Clone();
        }
        return true;
    }

    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken cancellationToken)
    {
        PeerReplicatedStateSnapshot snapshot;
        lock (sync) snapshot = new PeerReplicatedStateSnapshot(
            values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal), appliedCommands.ToArray());
        await using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(snapshot));
        await writer.CopyFromAsync(stream, stream.Length, cancellationToken);
    }

    protected override async ValueTask RestoreAsync(FileInfo snapshot, CancellationToken cancellationToken)
    {
        await using var stream = snapshot.OpenRead();
        var restored = await JsonSerializer.DeserializeAsync<PeerReplicatedStateSnapshot>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Instantané Raft illisible.");
        lock (sync)
        {
            values.Clear();
            foreach (var (key, value) in restored.Values) values[key] = value.Clone();
            appliedCommands.Clear();
            appliedCommands.UnionWith(restored.AppliedCommandIds);
        }
    }
}
