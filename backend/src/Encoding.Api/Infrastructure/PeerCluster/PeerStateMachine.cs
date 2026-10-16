using System.Text.Json;
using System.Data.Common;
using Encoding.Api.Infrastructure.Data;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerReplicatedStateCommand(Guid CommandId, string Key, JsonElement Value);
public sealed record PeerReplicatedStateSnapshot(Dictionary<string, JsonElement> Values, Guid[] AppliedCommandIds);

/// <summary>Durable replicated key/value projection used by peer-cluster commands.</summary>
public sealed class PeerStateMachine : SimpleStateMachine
{
    public const int MaximumCommandBytes = 4 * 1_048_576;
    private readonly object sync = new();
    private readonly DbDataSource? dataSource;
    private readonly Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> appliedCommands = [];

    public PeerStateMachine(PeerClusterOptions options, DbDataSource? dataSource = null)
        : base(new DirectoryInfo(Path.Combine(options.DataDirectory, "raft", "snapshots")))
    {
        this.dataSource = dataSource;
    }

    public JsonElement? Get(string key)
    {
        lock (sync) return values.TryGetValue(key, out var value) ? value.Clone() : null;
    }

    public IReadOnlyList<KeyValuePair<string, JsonElement>> List(string prefix)
    {
        lock (sync) return values.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonElement>(pair.Key, pair.Value.Clone())).ToArray();
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
        lock (sync) if (appliedCommands.Contains(command.CommandId)) return false;
        PeerFlowDraftState? draftHeadUpdate = null;
        if (PeerBusinessState.TryReadLibraryKey(command.Key, out var libraryId))
        {
            if (!PeerBusinessState.TryParseLibraryDocument(command.Value, out var document))
                throw new InvalidDataException("Projection de bibliothèque répliquée invalide.");
            await ApplyLibraryProjectionAsync(libraryId, document, cancellationToken);
        }
        else if (PeerBusinessState.TryReadFlowVersionKey(command.Key, out var flowId, out var flowVersion))
        {
            if (!PeerBusinessState.TryParseFlowVersion(command.Value, out var state)
                || state.Definition.Id != flowId || state.Definition.Version != flowVersion)
                throw new InvalidDataException("Projection de flow publiée invalide.");
            await ApplyFlowVersionProjectionAsync(state, cancellationToken);
            if (state.ExpectedDraftRevision is { } expectedDraft
                && GetFlowDraft(flowId) is { HasDraft: true } draft && draft.Revision == expectedDraft)
            {
                draftHeadUpdate = draft with { HasDraft = false, PublishedVersion = flowVersion };
                await ApplyFlowDraftProjectionAsync(draftHeadUpdate, cancellationToken);
            }
        }
        else if (PeerBusinessState.TryReadFlowDraftKey(command.Key, out var draftFlowId))
        {
            if (!PeerBusinessState.TryParseFlowDraft(command.Value, out var draft) || draft.FlowId != draftFlowId)
                throw new InvalidDataException("Projection de brouillon de flow invalide.");
            await ApplyFlowDraftProjectionAsync(draft, cancellationToken);
            draftHeadUpdate = draft;
        }
        else if (PeerBusinessState.TryReadLibraryWorkflowKey(command.Key, out var workflowLibraryId))
        {
            if (!PeerBusinessState.TryParseLibraryWorkflow(command.Value, out var workflow) || workflow.LibraryId != workflowLibraryId)
                throw new InvalidDataException("Projection d’assignation de flow invalide.");
            await ApplyLibraryWorkflowProjectionAsync(workflow, cancellationToken);
        }
        else if (command.Key.StartsWith("business/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Type de projection métier non pris en charge.");
        }
        lock (sync)
        {
            if (!appliedCommands.Add(command.CommandId)) return false;
            values[command.Key] = command.Value.Clone();
            if (draftHeadUpdate is not null)
                values[PeerBusinessState.FlowDraftKey(draftHeadUpdate.FlowId)] = JsonSerializer.SerializeToElement(draftHeadUpdate, JsonOptions);
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
        foreach (var (key, value) in restored.Values.OrderBy(pair => RestorePriority(pair.Key)).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (PeerBusinessState.TryReadLibraryKey(key, out var libraryId))
            {
                if (!PeerBusinessState.TryParseLibraryDocument(value, out var document))
                    throw new InvalidDataException("Instantané de bibliothèque répliquée invalide.");
                await ApplyLibraryProjectionAsync(libraryId, document, cancellationToken);
            }
            else if (PeerBusinessState.TryReadFlowVersionKey(key, out var flowId, out var flowVersion))
            {
                if (!PeerBusinessState.TryParseFlowVersion(value, out var state)
                    || state.Definition.Id != flowId || state.Definition.Version != flowVersion)
                    throw new InvalidDataException("Instantané de flow publié invalide.");
                await ApplyFlowVersionProjectionAsync(state, cancellationToken);
            }
            else if (PeerBusinessState.TryReadFlowDraftKey(key, out var draftId))
            {
                if (!PeerBusinessState.TryParseFlowDraft(value, out var draft) || draft.FlowId != draftId)
                    throw new InvalidDataException("Instantané de brouillon de flow invalide.");
                await ApplyFlowDraftProjectionAsync(draft, cancellationToken);
            }
            else if (PeerBusinessState.TryReadLibraryWorkflowKey(key, out var workflowLibraryId))
            {
                if (!PeerBusinessState.TryParseLibraryWorkflow(value, out var workflow) || workflow.LibraryId != workflowLibraryId)
                    throw new InvalidDataException("Instantané d’assignation de flow invalide.");
                await ApplyLibraryWorkflowProjectionAsync(workflow, cancellationToken);
            }
        }
    }

    private async Task ApplyLibraryProjectionAsync(Guid id, PeerBusinessStateDocument document, CancellationToken cancellationToken)
    {
        if (dataSource is null) return;
        if (document.Deleted)
        {
            await using var deleteConnection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var delete = Sql.Command("UPDATE libraries SET is_active=false,is_deleted=true WHERE id=@id", deleteConnection);
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        var library = document.Value.Deserialize<PeerLibraryState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Projection de bibliothèque illisible.");
        if (library.Id != id || string.IsNullOrWhiteSpace(library.Name) || library.Name.Length > 128)
            throw new InvalidDataException("Identité de bibliothèque répliquée invalide.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var upsert = Sql.Command("""
            INSERT INTO libraries(id,name,is_active,created_at,is_deleted) VALUES(@id,@name,@active,@created,false)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,is_active=excluded.is_active,created_at=excluded.created_at,is_deleted=false
            """, connection);
        upsert.Parameters.AddWithValue("id", library.Id);
        upsert.Parameters.AddWithValue("name", library.Name);
        upsert.Parameters.AddWithValue("active", library.IsActive);
        upsert.Parameters.AddWithValue("created", library.CreatedAt);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private PeerFlowDraftState? GetFlowDraft(Guid id)
    {
        var value = Get(PeerBusinessState.FlowDraftKey(id));
        return value is not null && PeerBusinessState.TryParseFlowDraft(value.Value, out var state) ? state : null;
    }

    private async Task ApplyFlowVersionProjectionAsync(PeerFlowVersionState state, CancellationToken cancellationToken)
    {
        if (dataSource is null) return;
        var id = state.Definition.Id;
        var version = state.Definition.Version;
        var maximum = Sql.IsSqlite(dataSource) ? "MAX(flows.current_version,excluded.current_version)" : "GREATEST(flows.current_version,excluded.current_version)";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var flow = Sql.Command($"""
            INSERT INTO flows(id,current_version,updated_at) VALUES(@id,@version,@created)
            ON CONFLICT(id) DO UPDATE SET current_version={maximum},
                updated_at=CASE WHEN flows.current_version<excluded.current_version THEN excluded.updated_at ELSE flows.updated_at END
            """, connection, transaction))
        {
            flow.Parameters.AddWithValue("id", id);
            flow.Parameters.AddWithValue("version", version);
            flow.Parameters.AddWithValue("created", state.CreatedAt);
            await flow.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var published = Sql.Command("""
            INSERT INTO flow_versions(flow_id,version,definition,created_at) VALUES(@id,@version,@definition,@created)
            ON CONFLICT(flow_id,version) DO NOTHING;
            INSERT INTO flow_backup_jobs(flow_id,version) VALUES(@id,@version) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            published.Parameters.AddWithValue("id", id);
            published.Parameters.AddWithValue("version", version);
            published.Parameters.AddWithValue("definition", SqlKind.Json, JsonSerializer.Serialize(state.Definition, JsonOptions));
            published.Parameters.AddWithValue("created", state.CreatedAt);
            await published.ExecuteNonQueryAsync(cancellationToken);
        }
        if (state.ExpectedDraftRevision is { } revision)
        {
            await using var deleteDraft = Sql.Command("DELETE FROM flow_drafts WHERE flow_id=@id AND revision=@revision", connection, transaction);
            deleteDraft.Parameters.AddWithValue("id", id);
            deleteDraft.Parameters.AddWithValue("revision", revision);
            await deleteDraft.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ApplyFlowDraftProjectionAsync(PeerFlowDraftState state, CancellationToken cancellationToken)
    {
        if (dataSource is null) return;
        var maximum = Sql.IsSqlite(dataSource) ? "MAX(flows.current_version,excluded.current_version)" : "GREATEST(flows.current_version,excluded.current_version)";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var flow = Sql.Command($"""
            INSERT INTO flows(id,current_version,updated_at) VALUES(@id,@version,@updated)
            ON CONFLICT(id) DO UPDATE SET current_version={maximum}
            """, connection, transaction))
        {
            flow.Parameters.AddWithValue("id", state.FlowId);
            flow.Parameters.AddWithValue("version", state.PublishedVersion);
            flow.Parameters.AddWithValue("updated", state.UpdatedAt);
            await flow.ExecuteNonQueryAsync(cancellationToken);
        }
        if (state.HasDraft)
        {
            var definition = state.Definition ?? throw new InvalidDataException("Le brouillon de flow est absent.");
            await using (var draft = Sql.Command("""
                INSERT INTO flow_drafts(flow_id,revision,definition,updated_at) VALUES(@id,@revision,@definition,@updated)
                ON CONFLICT(flow_id) DO UPDATE SET revision=excluded.revision,definition=excluded.definition,updated_at=excluded.updated_at
                """, connection, transaction))
            {
                draft.Parameters.AddWithValue("id", state.FlowId);
                draft.Parameters.AddWithValue("revision", state.Revision);
                draft.Parameters.AddWithValue("definition", SqlKind.Json, JsonSerializer.Serialize(definition, JsonOptions));
                draft.Parameters.AddWithValue("updated", state.UpdatedAt);
                await draft.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var backup = Sql.Command("INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@id,@revision,@definition) ON CONFLICT DO NOTHING", connection, transaction);
            backup.Parameters.AddWithValue("id", state.FlowId);
            backup.Parameters.AddWithValue("revision", state.Revision);
            backup.Parameters.AddWithValue("definition", SqlKind.Json, JsonSerializer.Serialize(definition, JsonOptions));
            await backup.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var deleteDraft = Sql.Command("DELETE FROM flow_drafts WHERE flow_id=@id AND revision=@revision", connection, transaction);
            deleteDraft.Parameters.AddWithValue("id", state.FlowId);
            deleteDraft.Parameters.AddWithValue("revision", state.Revision);
            await deleteDraft.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ApplyLibraryWorkflowProjectionAsync(PeerLibraryWorkflowState state, CancellationToken cancellationToken)
    {
        if (dataSource is null) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (state.FlowId is { } flowId)
        {
            await using var assignment = Sql.Command("""
                INSERT INTO library_flow_assignments(library_id,flow_id,flow_version,revision) VALUES(@library,@flow,@version,@revision)
                ON CONFLICT(library_id) DO UPDATE SET flow_id=excluded.flow_id,flow_version=excluded.flow_version,revision=excluded.revision
                """, connection, transaction);
            assignment.Parameters.AddWithValue("library", state.LibraryId);
            assignment.Parameters.AddWithValue("flow", flowId);
            assignment.Parameters.AddWithValue("version", state.FlowVersion!.Value);
            assignment.Parameters.AddWithValue("revision", state.Revision);
            await assignment.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var delete = Sql.Command("DELETE FROM library_flow_assignments WHERE library_id=@library", connection, transaction);
            delete.Parameters.AddWithValue("library", state.LibraryId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var revision = Sql.Command("UPDATE libraries SET flow_assignment_revision=@revision WHERE id=@library", connection, transaction))
        {
            revision.Parameters.AddWithValue("library", state.LibraryId);
            revision.Parameters.AddWithValue("revision", state.Revision);
            await revision.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static int RestorePriority(string key) => PeerBusinessState.TryReadLibraryKey(key, out _) ? 0
        : PeerBusinessState.TryReadFlowVersionKey(key, out _, out _) ? 1
        : PeerBusinessState.TryReadFlowDraftKey(key, out _) ? 2
        : PeerBusinessState.TryReadLibraryWorkflowKey(key, out _) ? 3 : 4;
}
