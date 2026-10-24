using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Tools;
using Encoding.Contracts.Cluster;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Réconcilie les données durables du cluster sous forme d'entités LWW. Les files d'exécution, baux,
/// réservations, sessions et secrets DP locaux restent en dehors de cette liste.
/// </summary>
public sealed partial class ClusterEntityReconciliation(
    DbDataSource dataSource,
    ClusterDataKeyStore dataKeyStore,
    IDataProtectionProvider protection,
    ILogger<ClusterEntityReconciliation> logger,
    ClusterPeerAdmissionRepository? peerAdmissions = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector pluginProtector = protection.CreateProtector("Encoding.PluginConfiguration.v1");
    private readonly IDataProtector aiCredentialProtector = protection.CreateProtector(AiCredentialStore.Purpose);
    private IDataProtector LocalProtector(string table) => table == "ai_credentials" ? aiCredentialProtector : pluginProtector;
    private static readonly HashSet<string> BooleanColumns = new(StringComparer.Ordinal)
    {
        "singleton", "idempotent", "is_active", "draining", "quarantined", "can_read", "can_write", "is_enabled",
        "watch_enabled", "schedule_enabled", "enabled", "has_more"
    };
    private static readonly EntityTable[] Tables =
    [
        new("cluster_nodes", ["id", "name", "roles", "max_concurrent_tasks", "capabilities", "last_seen_at", "created_at", "deleted_at", "display_name", "labels", "inventory", "desired_roles", "draining", "quarantined", "incompatible_reason", "protocol_version", "node_token_hash"], ["id"], ["capabilities", "labels", "inventory"], ["roles", "desired_roles"], ["node_token_hash"], ["id", "name", "roles", "max_concurrent_tasks", "capabilities", "created_at", "deleted_at", "display_name", "labels", "inventory", "desired_roles", "draining", "quarantined", "incompatible_reason", "protocol_version", "node_token_hash"]),
        new("node_enrollment_requests", ["node_id", "pairing_key_hash", "node_token_hash", "peer_id", "peer_public_key", "name", "roles", "max_concurrent_tasks", "capabilities", "created_at", "expires_at", "inventory", "display_name"], ["node_id"], ["capabilities", "inventory"], ["roles"], ["pairing_key_hash", "node_token_hash"]),
        new("cluster_peer_admissions", ["cluster_id", "peer_id", "node_id", "public_key", "approved_by_peer_id", "wrapped_data_key", "key_fingerprint", "issued_at", "revoked_at", "signature"], ["cluster_id", "peer_id"], immutable: false, detectDeletes: false),
        new("node_groups", ["id", "name", "description", "mode", "criteria", "labels", "created_at", "updated_at"], ["id"], ["criteria", "labels"]),
        new("node_directory_browse_requests", ["id", "node_id", "path", "state", "entries", "has_more", "error", "created_at", "updated_at", "expires_at", "claim_until"], ["id"], ["entries"]),
        new("libraries", ["id", "name", "created_at", "is_active", "flow_assignment_revision"], ["id"]),
        new("flows", ["id", "current_version", "updated_at", "deleted_at"], ["id"]),
        new("flow_scripts", ["id", "draft_revision", "draft_name", "draft_language", "draft_description", "draft_code", "published_version", "created_at", "updated_at"], ["id"]),
        new("encoding_profiles", ["id", "draft_revision", "draft_name", "draft_description", "draft_targets", "published_version", "created_at", "updated_at"], ["id"], ["draft_targets"]),
        new("users", ["id", "username", "password_hash", "role", "is_active", "failed_attempts", "locked_until", "created_at", "updated_at"], ["id"]),
        new("api_tokens", ["id", "name", "token_hash", "token_prefix", "role", "is_active", "created_at", "expires_at", "last_used_at", "revoked_at", "scopes"], ["id"], binaryColumns: ["token_hash"]),
        new("ai_credentials", ["provider", "revision", "encrypted_values", "updated_at"], ["provider"], binaryColumns: ["encrypted_values"]),
        new("plugin_configurations", ["plugin_id", "revision", "encrypted_values", "updated_at"], ["plugin_id"], binaryColumns: ["encrypted_values"]),
        new("flow_plugin_configurations", ["flow_id", "plugin_id", "revision", "encrypted_values", "updated_at"], ["flow_id", "plugin_id"], binaryColumns: ["encrypted_values"]),
        new("enrollment_window", ["id", "open_until"], ["id"]),
        new("first_link_acceptance", ["id", "expires_at", "enabled_by"], ["id"]),
        new("node_processing_settings", ["node_id", "max_concurrent_tasks", "revision", "schedule_enabled", "schedule_timezone", "schedule_slots"], ["node_id"]),
        new("node_group_members", ["group_id", "node_id"], ["group_id", "node_id"]),
        new("flow_versions", ["flow_id", "version", "definition", "created_at"], ["flow_id", "version"], ["definition"], immutable: true),
        // The backup file and its retry status are server-local. Replicate only the outbox identity;
        // each server will create and verify its own snapshot from the replicated definition.
        new("flow_backup_jobs", ["flow_id", "version"], ["flow_id", "version"], immutable: true, detectDeletes: false),
        new("flow_drafts", ["flow_id", "revision", "definition", "updated_at"], ["flow_id"], ["definition"]),
        // Historical draft definitions are cluster data; status/check timestamps remain local.
        new("flow_draft_backups", ["flow_id", "revision", "definition"], ["flow_id", "revision"], ["definition"], detectDeletes: false),
        new("flow_script_versions", ["script_id", "version", "name", "language", "description", "code", "created_at"], ["script_id", "version"], immutable: true),
        new("encoding_profile_versions", ["profile_id", "version", "name", "description", "targets", "created_at"], ["profile_id", "version"], ["targets"], immutable: true),
        new("flow_resources", ["id", "kind", "name", "description", "created_at", "updated_at"], ["id"]),
        new("flow_resource_versions", ["resource_id", "version", "files", "total_bytes", "created_at"], ["resource_id", "version"], ["files"], immutable: true),
        new("flow_templates", ["id", "name", "description", "source_flow_id", "source_version", "source_flow_name", "media_type", "definition", "created_at"], ["id"], ["definition"]),
        new("flow_component_packs", ["id", "name", "description", "source_flow_id", "source_version", "source_flow_name", "media_type", "input_node_id", "definition", "outputs", "created_at"], ["id"], ["definition", "outputs"]),
        new("library_flow_assignments", ["library_id", "flow_id", "flow_version", "revision"], ["library_id"]),
        new("library_node_accesses", ["library_id", "node_id", "mode", "local_path", "can_read", "can_write", "transfer_source_node_id", "updated_at", "is_enabled"], ["library_id", "node_id"]),
        new("library_node_processing_limits", ["library_id", "node_id", "max_concurrent_tasks"], ["library_id", "node_id"]),
        new("library_processing_settings", ["library_id", "watch_enabled", "scan_interval_seconds", "max_concurrent_tasks", "revision"], ["library_id"]),
        new("library_replacement_policies", ["library_id", "enabled", "grace_period_hours", "revision"], ["library_id"]),
        new("executions", ["id", "media", "library_name", "flow_name", "flow_version", "flow_definition", "state", "node_id", "progress", "detail", "attempt", "retry_of", "revision", "queued_at", "started_at", "finished_at", "updated_at"], ["id"], ["flow_definition"], orderBy: ["queued_at", "id"], detectDeletes: false, deltaColumn: "updated_at"),
        new("execution_events", ["id", "execution_id", "revision", "state", "detail", "created_at"], ["id"], immutable: true, detectDeletes: false, deltaColumn: "created_at"),
        new("execution_steps", ["execution_id", "node_id", "invocation_id", "started_at", "completed_at", "completed_revision", "idempotent", "checkpoint", "completed_epoch", "completed_worker_node_id", "command_logs"], ["execution_id", "node_id"], ["checkpoint", "command_logs"]),
        new("execution_artifacts", ["execution_id", "sha256", "byte_length"], ["execution_id", "sha256"], immutable: true),
        new("execution_inputs", ["execution_id", "input_path", "sha256"], ["execution_id", "input_path"], immutable: true),
        new("library_scans", ["id", "library_id", "media_type", "state", "node_id", "created_at", "completed_at", "detail"], ["id"], filter: "state = 'completed' AND id = (SELECT latest.id FROM library_scans latest WHERE latest.library_id=library_scans.library_id AND latest.state='completed' ORDER BY latest.completed_at DESC,latest.id DESC LIMIT 1)", immutable: true, detectDeletes: false),
        new("media_replacement_marks", ["library_id", "relative_path", "byte_length", "modified_at", "execution_id"], ["library_id", "relative_path", "byte_length", "modified_at"], immutable: true)
    ];
    private static readonly EntityTable MediaTable = new("library_media", ["id", "library_id", "relative_path", "byte_length", "modified_at", "scan_id", "stable_since"], ["library_id", "relative_path"], fingerprintColumns: ["library_id", "relative_path", "byte_length", "modified_at", "stable_since"], preserveColumnsOnUpdate: ["id", "scan_id"]);
    // Receipts use the media path as their cross-node identity. SQLite nodes may assign different
    // library_media IDs when they scan the same file independently.
    private static readonly EntityTable ProcessingReceiptTable = new("library_processing_receipts",
        ["library_id", "relative_path", "flow_id", "flow_version", "byte_length", "modified_at", "execution_id"],
        ["library_id", "relative_path", "flow_id", "flow_version", "byte_length", "modified_at"],
        fingerprintColumns: ["library_id", "relative_path", "flow_id", "flow_version", "byte_length", "modified_at", "execution_id"],
        detectDeletes: false);

    public const int MaximumCaptureBatchSize = 64;

    private const string EntitySetType = "entity.set";
    private const string EntityDeleteType = "entity.delete";
    private const string NodePresenceType = "node.presence";

    /// <summary>Capture les changements locaux depuis le dernier passage et les ajoute au journal avant le prochain échange.</summary>
    public async Task<int> CaptureLocalChangesAsync(CancellationToken ct)
    {
        var snapshots = new List<EntitySnapshot>();
        var presence = new List<NodePresenceSnapshot>();
        var changedScans = new List<(string LibraryId, string ScanId)>();
        var preparedMedia = new Dictionary<(string LibraryId, string ScanId), List<EntitySnapshot>>();
        DateTimeOffset? receiptMaxCursor = null;
        Dictionary<string, CaptureSelection> selections;
        await using (var readConnection = await dataSource.OpenConnectionAsync(ct))
        {
            await using var readTransaction = await readConnection.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, ct);
            selections = await ReadCaptureSelectionsAsync(readConnection, readTransaction, ct);
            foreach (var table in Tables.Where(table => selections.ContainsKey(table.Name)))
            {
                var selection = selections[table.Name];
                var predicates = new List<string>();
                if (table.Filter is not null) predicates.Add(table.Filter);
                if (!selection.FullScan) predicates.Add(SelectedKeysPredicate(table));
                var where = predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);
                await using var command = Sql.Command($"SELECT {string.Join(",", table.Columns)} FROM {table.Name}{where}{(table.OrderBy.Length == 0 ? "" : " ORDER BY " + string.Join(",", table.OrderBy))}", readConnection);
                BindSelectedKeys(command, selection);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var row = ReadRow(reader, table);
                    var key = MakeKey(row, table.KeyColumns);
                    DateTimeOffset? cursorValue = null;
                    if (table.DeltaColumn is not null)
                    {
                        var ordinal = Array.IndexOf(table.Columns, table.DeltaColumn);
                        if (ordinal >= 0 && reader.GetValue(ordinal) is not DBNull) cursorValue = ReadInstant(reader.GetValue(ordinal));
                    }
                    snapshots.Add(new(table, key, row, Fingerprint(row, table), cursorValue));
                    if (table.Name == "cluster_nodes")
                        presence.Add(new(key, row["last_seen_at"]?.GetValue<string>() ?? "", row["id"]?.GetValue<string>() ?? ""));
                }
            }

            // A receipt refers to library_media.id, which is local to a scan on SQLite. Carry its
            // stable path instead and resolve the destination's local media ID when projecting it.
            if (selections.TryGetValue(ProcessingReceiptTable.Name, out var receiptSelection))
            {
                var receiptFilter = receiptSelection.FullScan ? "" : " WHERE " + SelectedKeysPredicate(ProcessingReceiptTable, "r");
                await using var receipts = Sql.Command($"""
                    SELECT r.library_id,m.relative_path,r.flow_id,r.flow_version,r.byte_length,r.modified_at,r.execution_id,e.updated_at
                    FROM library_processing_receipts r
                    JOIN library_media m ON m.id=r.file_id
                    JOIN executions e ON e.id=r.execution_id
                    {receiptFilter}
                    ORDER BY e.updated_at,r.library_id,m.relative_path
                    """, readConnection);
                BindSelectedKeys(receipts, receiptSelection);
                await using (var reader = await receipts.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var row = ReadRow(reader, ProcessingReceiptTable);
                        var key = MakeKey(row, ProcessingReceiptTable.KeyColumns);
                        var cursor = ReadInstant(reader.GetValue(7));
                        if (receiptMaxCursor is null || cursor > receiptMaxCursor) receiptMaxCursor = cursor;
                        snapshots.Add(new(ProcessingReceiptTable, key, row, Fingerprint(row, ProcessingReceiptTable), cursor));
                    }
                }
            }

            // Inventory hashing is done before writer admission. A completed scan is checkpointed
            // only after all its media events have committed, so cancellation between batches is resumable.
            foreach (var scan in snapshots.Where(item => item.Table.Name == "library_scans"))
            {
                var libraryId = scan.Row["library_id"]!.GetValue<string>();
                var scanId = scan.Row["id"]!.GetValue<string>();
                if (await ReadTableCursorAsync(readConnection, MediaInventoryCursor(scanId), ct) is not null) continue;
                var mediaRows = new List<EntitySnapshot>();
                await using var media = Sql.Command($"SELECT {string.Join(",", MediaTable.Columns)} FROM library_media WHERE library_id=@library AND scan_id=@scan ORDER BY relative_path", readConnection);
                media.Parameters.AddWithValue("library", Guid.Parse(libraryId));
                media.Parameters.AddWithValue("scan", Guid.Parse(scanId));
                await using var reader = await media.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var row = ReadRow(reader, MediaTable);
                    mediaRows.Add(new(MediaTable, MakeKey(row, MediaTable.KeyColumns), row, Fingerprint(row, MediaTable), null));
                }
                preparedMedia[(libraryId, scanId)] = mediaRows;
            }

        }

        logger.LogDebug(new EventId(9010, "CaptureSelectionRead"), "Capture locale : {Tables} tables modifiées, {Rows} lignes préparées.", selections.Count, snapshots.Count);
        if (selections.Count == 0) return 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var capture = await CaptureWriter.CreateAsync(this, connection, ct);
        var local = capture.Identity;
        var captured = 0;
        foreach (var table in Tables.Where(table => selections.ContainsKey(table.Name)))
        {
            var selection = selections[table.Name];
            var tableGroup = snapshots.Where(item => item.Table.Name == table.Name).ToArray();
            var existing = await ReadEntityStatesAsync(connection, capture.Transaction, table.Name, ct, selection.FullScan ? null : selection.Keys);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snapshot in tableGroup)
            {
                seen.Add(snapshot.Key);
                if (table.Name == "library_scans")
                {
                    var scanId = snapshot.Row["id"]!.GetValue<string>();
                    if (await ReadTableCursorAsync(connection, MediaInventoryCursor(scanId), ct) is null)
                        changedScans.Add((snapshot.Row["library_id"]!.GetValue<string>(), scanId));
                }
                if (existing.TryGetValue(snapshot.Key, out var state) && !state.IsDeleted && state.Fingerprint == snapshot.Fingerprint) continue;
                if (!await CanCaptureAsync(snapshot, connection, capture.Transaction, ct)) continue;
                var occurredAt = DateTimeOffset.UtcNow;
                var sequence = ++capture.NextSequence;
                var payload = CreateSetPayload(snapshot, occurredAt, local.ClusterId);
                await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntitySetType, payload, occurredAt, ct);
                await UpsertEntityStateAsync(connection, capture.Transaction, table.Name, snapshot.Key, snapshot.Fingerprint, occurredAt, local.OriginId, sequence, false, ct);
                captured++;
                await capture.AdvanceAsync(ct);
            }
            foreach (var (key, state) in existing)
            {
                if (!table.DetectDeletes || state.IsDeleted || seen.Contains(key)) continue;
                if (!await CanDeleteAsync(table, key, connection, capture.Transaction, ct)) continue;
                var occurredAt = DateTimeOffset.UtcNow;
                var sequence = ++capture.NextSequence;
                var payload = JsonSerializer.Serialize(new EntityDeleteEnvelope(table.Name, key), JsonOptions);
                await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntityDeleteType, payload, occurredAt, ct);
                await UpsertEntityStateAsync(connection, capture.Transaction, table.Name, key, "deleted", occurredAt, local.OriginId, sequence, true, ct);
                captured++;
                await capture.AdvanceAsync(ct);
            }
            if (table.DeltaColumn is not null && tableGroup.Select(item => item.CursorValue).Where(value => value.HasValue).Max() is { } maxCursor)
                await UpsertTableCursorAsync(connection, capture.Transaction, table.Name, maxCursor, ct);
        }

        var receiptStates = selections.TryGetValue(ProcessingReceiptTable.Name, out var receiptWork)
            ? await ReadEntityStatesAsync(connection, capture.Transaction, ProcessingReceiptTable.Name, ct, receiptWork.FullScan ? null : receiptWork.Keys)
            : new Dictionary<string, EntityState>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots.Where(item => item.Table.Name == ProcessingReceiptTable.Name))
        {
            if (receiptStates.TryGetValue(snapshot.Key, out var state) && !state.IsDeleted && state.Fingerprint == snapshot.Fingerprint) continue;
            if (!await CanCaptureAsync(snapshot, connection, capture.Transaction, ct)) continue;
            var occurredAt = DateTimeOffset.UtcNow;
            var sequence = ++capture.NextSequence;
            await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntitySetType, CreateSetPayload(snapshot, occurredAt, local.ClusterId), occurredAt, ct);
            await UpsertEntityStateAsync(connection, capture.Transaction, ProcessingReceiptTable.Name, snapshot.Key, snapshot.Fingerprint, occurredAt, local.OriginId, sequence, false, ct);
            captured++;
            await capture.AdvanceAsync(ct);
        }
        if (receiptMaxCursor is { } receiptCursorValue)
            await UpsertTableCursorAsync(connection, capture.Transaction, ProcessingReceiptTable.Name, receiptCursorValue, ct);

        var mediaStates = changedScans.Count == 0 ? new Dictionary<string, EntityState>(StringComparer.Ordinal) : await ReadEntityStatesAsync(connection, capture.Transaction, MediaTable.Name, ct);
        foreach (var (libraryId, scanId) in changedScans)
        {
            if (!preparedMedia.TryGetValue((libraryId, scanId), out var mediaSnapshots)) continue;
            var seenMedia = mediaSnapshots.Select(snapshot => snapshot.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var snapshot in mediaSnapshots)
            {
                if (mediaStates.TryGetValue(snapshot.Key, out var mediaState) && !mediaState.IsDeleted && mediaState.Fingerprint == snapshot.Fingerprint) continue;
                if (!await CanCaptureAsync(snapshot, connection, capture.Transaction, ct)) continue;
                var occurredAt = DateTimeOffset.UtcNow;
                var sequence = ++capture.NextSequence;
                var payload = CreateSetPayload(snapshot, occurredAt, local.ClusterId);
                await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntitySetType, payload, occurredAt, ct);
                await UpsertEntityStateAsync(connection, capture.Transaction, MediaTable.Name, snapshot.Key, snapshot.Fingerprint, occurredAt, local.OriginId, sequence, false, ct);
                captured++;
                await capture.AdvanceAsync(ct);
            }
            foreach (var (key, state) in mediaStates)
            {
                if (state.IsDeleted || seenMedia.Contains(key) || !KeyBelongsToLibrary(key, libraryId)) continue;
                if (!await CanDeleteAsync(MediaTable, key, connection, capture.Transaction, ct)) continue;
                var occurredAt = DateTimeOffset.UtcNow;
                var sequence = ++capture.NextSequence;
                var payload = JsonSerializer.Serialize(new EntityDeleteEnvelope(MediaTable.Name, key), JsonOptions);
                await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntityDeleteType, payload, occurredAt, ct);
                await UpsertEntityStateAsync(connection, capture.Transaction, MediaTable.Name, key, "deleted", occurredAt, local.OriginId, sequence, true, ct);
                captured++;
                await capture.AdvanceAsync(ct);
            }
            await UpsertTableCursorAsync(connection, capture.Transaction, MediaInventoryCursor(scanId), DateTimeOffset.UtcNow, ct);

        }

        foreach (var snapshot in presence)
        {
            if (string.IsNullOrWhiteSpace(snapshot.LastSeenAt) || !DateTimeOffset.TryParse(snapshot.LastSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seenAt)) continue;
            var key = JsonSerializer.Serialize(snapshot.NodeId);
            var fingerprint = Fingerprint(snapshot.LastSeenAt);
            var existing = await ReadEntityStateAsync(connection, capture.Transaction, "node-presence", key, ct);
            if (existing is { IsDeleted: false } state && state.Fingerprint == fingerprint) continue;
            await using (var currentPresence = Sql.Command("SELECT last_seen_at FROM cluster_nodes WHERE id=@id", connection, capture.Transaction))
            {
                currentPresence.Parameters.AddWithValue("id", Guid.Parse(snapshot.NodeId));
                var currentSeen = await currentPresence.ExecuteScalarAsync(ct);
                if (currentSeen is null or DBNull || ReadInstant(currentSeen) != seenAt.ToUniversalTime()) continue;
            }
            var sequence = ++capture.NextSequence;
            var occurredAt = seenAt.ToUniversalTime();
            var payload = JsonSerializer.Serialize(new NodePresenceEnvelope(snapshot.NodeId, snapshot.LastSeenAt), JsonOptions);
            await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, NodePresenceType, payload, occurredAt, ct);
            await UpsertEntityStateAsync(connection, capture.Transaction, "node-presence", key, fingerprint, occurredAt, local.OriginId, sequence, false, ct);
            captured++;
            await capture.AdvanceAsync(ct);
        }

        await AcknowledgeCaptureAsync(connection, capture.Transaction, selections.Values, ct);
        await capture.CommitAsync(ct);
        if (captured > 0) logger.LogInformation("{Count} changements locaux ajoutés au journal de réconciliation.", captured);
        return captured;
    }

    private static string MediaInventoryCursor(string scanId) => "scan-inventory/" + scanId;

    private async Task<bool> CanCaptureAsync(EntitySnapshot snapshot, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var current = await ReadEntityStateAsync(connection, transaction, snapshot.Table.Name, snapshot.Key, ct);
        if (current is { IsDeleted: false } state && state.Fingerprint == snapshot.Fingerprint) return false;
        var table = snapshot.Table;
        var select = table.Name == ProcessingReceiptTable.Name
            ? "SELECT r.library_id,m.relative_path,r.flow_id,r.flow_version,r.byte_length,r.modified_at,r.execution_id FROM library_processing_receipts r JOIN library_media m ON m.id=r.file_id"
            : $"SELECT {string.Join(",", table.Columns)} FROM {table.Name}";
        var predicate = table.Name == ProcessingReceiptTable.Name
            ? string.Join(" AND ", table.KeyColumns.Select(column => (column == "relative_path" ? "m." : "r.") + column + "=json_extract(@key, '$." + column + "')"))
            : KeyPredicate(table);
        await using var command = Sql.Command(select + " WHERE " + predicate, connection, transaction);
        command.Parameters.AddWithValue("key", snapshot.Key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && Fingerprint(ReadRow(reader, table), table) == snapshot.Fingerprint;
    }

    private async Task<bool> CanDeleteAsync(EntityTable table, string key, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var state = await ReadEntityStateAsync(connection, transaction, table.Name, key, ct);
        if (state is null or { IsDeleted: true }) return false;
        await using var exists = Sql.Command($"SELECT EXISTS(SELECT 1 FROM {table.Name} WHERE {KeyPredicate(table)})", connection, transaction);
        exists.Parameters.AddWithValue("key", key);
        return !await exists.ScalarBoolAsync(ct);
    }

    private static string KeyPredicate(EntityTable table) => string.Join(" AND ", table.KeyColumns.Select(column => column + "=json_extract(@key, '$." + column + "')"));

    private void CaptureBatchCommitted(int count) => logger.LogDebug(new EventId(9009, "CaptureBatchCommitted"), "Lot de capture validé : {Count} événements.", count);

    /// <summary>Each committed batch owns sequence allocation, events, entity states and finished cursors atomically.</summary>
    private sealed class CaptureWriter(ClusterEntityReconciliation owner, DbConnection connection) : IAsyncDisposable
    {
        private DbTransaction? transaction;
        private int count;
        public LocalIdentity Identity { get; private set; } = null!;
        public long NextSequence { get; set; }
        public DbTransaction Transaction => transaction ?? throw new InvalidOperationException("Lot de capture fermé.");

        public static async Task<CaptureWriter> CreateAsync(ClusterEntityReconciliation owner, DbConnection connection, CancellationToken ct)
        {
            var writer = new CaptureWriter(owner, connection);
            try { await writer.BeginAsync(ct); return writer; }
            catch { await writer.DisposeAsync(); throw; }
        }

        private async Task BeginAsync(CancellationToken ct)
        {
            transaction = await connection.BeginTransactionAsync(ct);
            var identity = await owner.ReadLocalIdentityAsync(connection, transaction, ct);
            if (Identity is not null && (identity.ClusterId != Identity.ClusterId || identity.OriginId != Identity.OriginId))
                throw new InvalidOperationException("L’identité du cluster a changé pendant la capture ; le prochain passage reprendra la capture.");
            Identity = identity;
            NextSequence = identity.NextSequence;
        }

        public async Task AdvanceAsync(CancellationToken ct)
        {
            if (++count < MaximumCaptureBatchSize) return;
            await CommitAsync(ct);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            await BeginAsync(ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            if (transaction is null) return;
            var finishing = transaction;
            transaction = null;
            try
            {
                if (NextSequence != Identity.NextSequence)
                {
                    await using var update = Sql.Command("UPDATE cluster_sync_identity SET next_sequence=@sequence WHERE singleton=true", connection, finishing);
                    update.Parameters.AddWithValue("sequence", NextSequence);
                    await update.ExecuteNonQueryAsync(ct);
                }
                await finishing.CommitAsync(ct);
            }
            finally { await finishing.DisposeAsync(); }
            owner.CaptureBatchCommitted(count);
            count = 0;
        }

        public async ValueTask DisposeAsync()
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    /// <summary>Projette les événements reçus. Les événements qui attendent une FK sont conservés et retentés.</summary>
    public async Task<int> ApplyPendingAsync(CancellationToken ct)
    {
        var applied = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            var pending = await ReadPendingAsync(ct);
            if (pending.Count == 0) break;
            var progress = 0;
            foreach (var change in pending.OrderBy(ProjectionRank).ThenBy(change => change.Change.OccurredAt).ThenBy(change => change.Change.Sequence))
            {
                ct.ThrowIfCancellationRequested();
                if (await ApplyOneAsync(change, ct)) { progress++; applied++; }
            }
            if (progress == 0) break;
        }
        return applied;
    }

    private async Task<IReadOnlyList<PendingChange>> ReadPendingAsync(CancellationToken ct)
    {
        var result = new List<PendingChange>();
        await using var command = dataSource.CreateCommand($"SELECT l.event_id,l.cluster_id,l.origin_id,l.sequence,l.event_type,l.payload,l.occurred_at FROM cluster_sync_pending_projections p JOIN cluster_change_log l ON l.event_id=p.event_id ORDER BY l.occurred_at,l.origin_id,l.sequence LIMIT {ClusterReconciliationRepository.MaximumBatchSize * 8}");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var change = new ClusterChangeEvent(ReadGuid(reader, 0), ReadGuid(reader, 1), ReadGuid(reader, 2),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture), reader.GetString(4), reader.GetString(5), ReadInstant(reader.GetValue(6)));
            result.Add(new PendingChange(change));
        }
        return result;
    }

    private async Task<bool> ApplyOneAsync(PendingChange pending, CancellationToken ct)
    {
        JsonObject? payload;
        try { payload = JsonNode.Parse(pending.Change.Payload) as JsonObject; }
        catch (JsonException) { return await DropPendingAsync(pending.Change.EventId, ct); }
        if (payload is null) return await DropPendingAsync(pending.Change.EventId, ct);

        string entityType;
        string key;
        string fingerprint;
        bool deleted;
        JsonObject? row = null;
        if (pending.Change.EventType == EntitySetType)
        {
            entityType = payload["table"]?.GetValue<string>() ?? "";
            row = payload["row"] as JsonObject;
            fingerprint = payload["fingerprint"]?.GetValue<string>() ?? "";
            deleted = false;
            if (!TryGetTable(entityType, out var table) || row is null
                || (entityType == "cluster_peer_admissions" && peerAdmissions is not null && !await peerAdmissions.VerifyIncomingGrantAsync(row, ct))
                || !TryUnprotectSensitive(pending.Change.ClusterId, entityType, row)) return await DropPendingAsync(pending.Change.EventId, ct);
            var incomingKey = payload["key"];
            key = MakeKey(row, table.KeyColumns);
            if (incomingKey is null || !JsonNode.DeepEquals(incomingKey, JsonNode.Parse(key))
                || string.IsNullOrWhiteSpace(fingerprint) || Fingerprint(row, table) != fingerprint)
                return await DropPendingAsync(pending.Change.EventId, ct);
        }
        else if (pending.Change.EventType == EntityDeleteType)
        {
            entityType = payload["table"]?.GetValue<string>() ?? "";
            key = payload["key"]?.GetValue<string>() ?? "";
            fingerprint = "deleted";
            deleted = true;
            if (!TryGetTable(entityType, out _) || string.IsNullOrWhiteSpace(key)) return await DropPendingAsync(pending.Change.EventId, ct);
        }
        else if (pending.Change.EventType == NodePresenceType)
        {
            entityType = "node-presence";
            var nodeId = payload["nodeId"]?.GetValue<string>() ?? "";
            var lastSeenAt = payload["lastSeenAt"]?.GetValue<string>() ?? "";
            if (!Guid.TryParse(nodeId, out _) || !DateTimeOffset.TryParse(lastSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                return await DropPendingAsync(pending.Change.EventId, ct);
            key = JsonSerializer.Serialize(nodeId);
            fingerprint = Fingerprint(lastSeenAt);
            deleted = false;
        }
        else return await DropPendingAsync(pending.Change.EventId, ct);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var sequenceLock = Sql.Command("SELECT origin_id FROM cluster_sync_identity WHERE singleton = true FOR UPDATE", connection, transaction))
            await sequenceLock.ExecuteScalarAsync(ct);
        var current = await ReadEntityStateAsync(connection, transaction, entityType, key, ct);
        if (current is { } currentState && CompareVersion(pending.Change.OccurredAt, pending.Change.OriginId, pending.Change.Sequence,
                currentState.OccurredAt, currentState.OriginId, currentState.Sequence) <= 0)
        {
            await DeletePendingAsync(connection, transaction, pending.Change.EventId, ct);
            await transaction.CommitAsync(ct);
            return true;
        }

        try
        {
            if (pending.Change.EventType == EntitySetType)
            {
                if (entityType == ProcessingReceiptTable.Name)
                {
                    if (!await UpsertProcessingReceiptAsync(connection, transaction, row!, ct)) return false;
                }
                else
                {
                    await UpsertRowAsync(connection, transaction, GetTable(entityType), row!, ct);
                    await EnsureFlowBackupProjectionAsync(entityType, row!, connection, transaction, ct);
                }
            }
            else if (pending.Change.EventType == EntityDeleteType)
            {
                if (entityType == ProcessingReceiptTable.Name)
                    await DeleteProcessingReceiptAsync(connection, transaction, payload["key"]!.GetValue<string>(), ct);
                else await DeleteRowAsync(connection, transaction, GetTable(entityType), payload["key"]!.GetValue<string>(), ct);
            }
            else
            {
                var nodeId = payload["nodeId"]!.GetValue<string>();
                var lastSeenAt = payload["lastSeenAt"]!.GetValue<string>();
                await using var update = Sql.Command("UPDATE cluster_nodes SET last_seen_at = CASE WHEN last_seen_at < @last_seen_at THEN @last_seen_at ELSE last_seen_at END WHERE id = @id", connection, transaction);
                update.Parameters.AddWithValue("last_seen_at", lastSeenAt);
                update.Parameters.AddWithValue("id", Guid.Parse(nodeId));
                if (await update.ExecuteNonQueryAsync(ct) == 0) return false;
            }
        }
        catch (DbException exception)
        {
            logger.LogDebug(exception, "Projection différée pour l'événement {EventId} ({EntityType}).", pending.Change.EventId, entityType);
            return false;
        }

        await UpsertEntityStateAsync(connection, transaction, entityType, key, fingerprint, pending.Change.OccurredAt, pending.Change.OriginId, pending.Change.Sequence, deleted, ct);
        await DeletePendingAsync(connection, transaction, pending.Change.EventId, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<bool> DropPendingAsync(Guid eventId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM cluster_sync_pending_projections WHERE event_id=@id");
        command.Parameters.AddWithValue("id", eventId);
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }

    private static int ProjectionRank(PendingChange pending)
    {
        try
        {
            if (pending.Change.EventType == NodePresenceType) return Tables.Length + 1;
            var tableName = JsonDocument.Parse(pending.Change.Payload).RootElement.GetProperty("table").GetString();
            var rank = Array.FindIndex(Tables, table => table.Name == tableName);
            if (tableName == "library_media") rank = Array.FindIndex(Tables, table => table.Name == "library_scans") + 1;
            if (tableName == ProcessingReceiptTable.Name) rank = Tables.Length + 2;
            return pending.Change.EventType == EntityDeleteType ? -rank : rank;
        }
        catch { return Tables.Length + 2; }
    }

    private JsonObject ReadRow(DbDataReader reader, EntityTable table)
    {
        var row = new JsonObject();
        for (var index = 0; index < table.Columns.Length; index++)
        {
            var value = reader.GetValue(index);
            var name = table.Columns[index];
            if (value is DBNull) { row[name] = null; continue; }
            if (table.JsonColumns.Contains(name, StringComparer.Ordinal))
            {
                var json = value switch
                {
                    JsonDocument document => document.RootElement.GetRawText(),
                    JsonElement element => element.GetRawText(),
                    string text => text,
                    _ => JsonSerializer.Serialize(value, JsonOptions)
                };
                try { row[name] = JsonNode.Parse(json); }
                catch (JsonException) { row[name] = JsonValue.Create(json); }
            }
            else if (table.ArrayColumns.Contains(name, StringComparer.Ordinal))
            {
                var items = value switch
                {
                    string[] array => array,
                    string json => JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [],
                    _ => []
                };
                row[name] = JsonSerializer.SerializeToNode(items, JsonOptions);
            }
            else if (table.BinaryColumns.Contains(name, StringComparer.Ordinal) && value is byte[] pluginCipher
                     && name == "encrypted_values" && (table.Name is "plugin_configurations" or "flow_plugin_configurations" or "ai_credentials"))
                row[name] = JsonNode.Parse(LocalProtector(table.Name).Unprotect(pluginCipher));
            else if (table.BinaryColumns.Contains(name, StringComparer.Ordinal) && value is byte[] bytes)
                row[name] = Convert.ToHexString(bytes).ToLowerInvariant();
            else if (BooleanColumns.Contains(name) || (name == "id" && (table.Name is "enrollment_window" or "first_link_acceptance")))
                row[name] = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            else
                row[name] = JsonSerializer.SerializeToNode(NormalizeValue(value), JsonOptions);
        }
        return row;
    }

    private static object? NormalizeValue(object value) => value switch
    {
        DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
        Guid id => id.ToString(),
        _ => value
    };

    private static string MakeKey(JsonObject row, string[] columns)
    {
        var key = new JsonObject();
        foreach (var column in columns) key[column] = row[column]?.DeepClone();
        return key.ToJsonString(JsonOptions);
    }

    private static string Fingerprint(JsonObject row, EntityTable table)
    {
        var included = table.FingerprintColumns ?? table.Columns;
        var stable = new JsonObject();
        foreach (var column in included) stable[column] = row[column]?.DeepClone();
        return Fingerprint(stable.ToJsonString(JsonOptions));
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string CreateSetPayload(EntitySnapshot snapshot, DateTimeOffset occurredAt, Guid clusterId)
    {
        var row = (JsonObject)snapshot.Row.DeepClone();
        ProtectSensitive(clusterId, snapshot.Table.Name, row);
        return new JsonObject
        {
            ["table"] = snapshot.Table.Name,
            ["key"] = JsonNode.Parse(snapshot.Key),
            ["fingerprint"] = snapshot.Fingerprint,
            ["occurredAt"] = occurredAt.ToString("O", CultureInfo.InvariantCulture),
            ["row"] = row
        }.ToJsonString(JsonOptions);
    }

    private void ProtectSensitive(Guid clusterId, string table, JsonObject row)
    {
        foreach (var column in SensitiveColumns(table))
        {
            if (!row.TryGetPropertyValue(column, out var value) || value is null || IsEncrypted(value)) continue;
            var raw = System.Text.Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions));
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[raw.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(DeriveKey(dataKeyStore.GetRequired(clusterId)), tag.Length);
            aes.Encrypt(nonce, raw, ciphertext, tag, System.Text.Encoding.UTF8.GetBytes(table + "." + column));
            row[column] = new JsonObject { ["encrypted"] = Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]) };
        }
    }

    private bool TryUnprotectSensitive(Guid clusterId, string table, JsonObject row)
    {
        try
        {
            foreach (var column in SensitiveColumns(table))
            {
                if (!row.TryGetPropertyValue(column, out var value) || value is null || !IsEncrypted(value)) continue;
                var packed = Convert.FromBase64String(value["encrypted"]!.GetValue<string>());
                if (packed.Length < 28) return false;
                var plaintext = new byte[packed.Length - 28];
                using var aes = new AesGcm(DeriveKey(dataKeyStore.GetRequired(clusterId)), 16);
                aes.Decrypt(packed.AsSpan(0, 12), packed.AsSpan(28), packed.AsSpan(12, 16), plaintext, System.Text.Encoding.UTF8.GetBytes(table + "." + column));
                row[column] = JsonNode.Parse(plaintext);
            }
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsEncrypted(JsonNode node) => node is JsonObject obj && obj.Count == 1 && obj.ContainsKey("encrypted");
    private static byte[] DeriveKey(ReadOnlySpan<byte> clusterDataKey) =>
        HMACSHA256.HashData(clusterDataKey, System.Text.Encoding.UTF8.GetBytes("Encoding.ClusterReconciliation.Data.v2"));

    private async Task UpsertRowAsync(DbConnection connection, DbTransaction transaction, EntityTable table, JsonObject row, CancellationToken ct)
    {
        var localRow = PrepareRowForLocalStorage(table, row);
        var expressions = table.Columns.Select(column => table.BinaryColumns.Contains(column, StringComparer.Ordinal) ? "@binary_" + column : "json_extract(@row, '$." + column + "')").ToArray();
        var updates = table.Columns.Where(column => !table.KeyColumns.Contains(column, StringComparer.Ordinal) && !table.PreserveColumnsOnUpdate.Contains(column, StringComparer.Ordinal)).Select(column => column == "last_seen_at" && table.Name == "cluster_nodes"
            ? "last_seen_at = CASE WHEN cluster_nodes.last_seen_at < excluded.last_seen_at THEN excluded.last_seen_at ELSE cluster_nodes.last_seen_at END"
            : $"{column}=excluded.{column}");
        var onConflict = table.Immutable ? "DO NOTHING" : "DO UPDATE SET " + string.Join(",", updates);
        var sql = $"INSERT INTO {table.Name} ({string.Join(",", table.Columns)}) SELECT {string.Join(",", expressions)} WHERE true ON CONFLICT ({string.Join(",", table.KeyColumns)}) {onConflict}";
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue("row", JsonKind(localRow));
        foreach (var column in table.BinaryColumns)
            command.Parameters.AddWithValue("binary_" + column, SqlKind.Bytes, ReadHex(localRow[column]));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> UpsertProcessingReceiptAsync(DbConnection connection, DbTransaction transaction, JsonObject row, CancellationToken ct)
    {
        var libraryId = Guid.Parse(row["library_id"]!.GetValue<string>());
        var path = row["relative_path"]!.GetValue<string>();
        object? mediaId;
        await using (var media = Sql.Command("SELECT id FROM library_media WHERE library_id=@library AND relative_path=@path", connection, transaction))
        {
            media.Parameters.AddWithValue("library", libraryId);
            media.Parameters.AddWithValue("path", path);
            mediaId = await media.ExecuteScalarAsync(ct);
        }
        if (mediaId is null or DBNull) return false;

        await using var receipt = Sql.Command("""
            INSERT INTO library_processing_receipts(library_id,file_id,flow_id,flow_version,byte_length,modified_at,execution_id)
            VALUES(@library,@file,@flow,@version,@bytes,@modified,@execution)
            ON CONFLICT(library_id,file_id,flow_id,flow_version,byte_length,modified_at) DO UPDATE SET execution_id=EXCLUDED.execution_id
            """, connection, transaction);
        receipt.Parameters.AddWithValue("library", libraryId);
        receipt.Parameters.AddWithValue("file", mediaId);
        receipt.Parameters.AddWithValue("flow", Guid.Parse(row["flow_id"]!.GetValue<string>()));
        receipt.Parameters.AddWithValue("version", row["flow_version"]!.GetValue<int>());
        receipt.Parameters.AddWithValue("bytes", row["byte_length"]!.GetValue<long>());
        receipt.Parameters.AddWithValue("modified", ReadInstant(row["modified_at"]!.GetValue<string>()));
        receipt.Parameters.AddWithValue("execution", Guid.Parse(row["execution_id"]!.GetValue<string>()));
        await receipt.ExecuteNonQueryAsync(ct);
        return true;
    }

    private static async Task EnsureFlowBackupProjectionAsync(string entityType, JsonObject row, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        if (entityType == "flow_versions")
        {
            await using var backup = Sql.Command("INSERT INTO flow_backup_jobs(flow_id,version) VALUES(@flow,@version) ON CONFLICT DO NOTHING", connection, transaction);
            backup.Parameters.AddWithValue("flow", Guid.Parse(row["flow_id"]!.GetValue<string>()));
            backup.Parameters.AddWithValue("version", row["version"]!.GetValue<int>());
            await backup.ExecuteNonQueryAsync(ct);
        }
        else if (entityType == "flow_drafts")
        {
            await using var backup = Sql.Command("INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@flow,@revision,@definition) ON CONFLICT DO NOTHING", connection, transaction);
            backup.Parameters.AddWithValue("flow", Guid.Parse(row["flow_id"]!.GetValue<string>()));
            backup.Parameters.AddWithValue("revision", row["revision"]!.GetValue<int>());
            backup.Parameters.AddWithValue("definition", SqlKind.Json, row["definition"]!.ToJsonString(JsonOptions));
            await backup.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task DeleteProcessingReceiptAsync(DbConnection connection, DbTransaction transaction, string keyJson, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(keyJson);
        var key = document.RootElement;
        var libraryId = Guid.Parse(key.GetProperty("library_id").GetString()!);
        await using var command = Sql.Command("""
            DELETE FROM library_processing_receipts
            WHERE library_id=@library
              AND file_id=(SELECT id FROM library_media WHERE library_id=@library AND relative_path=@path)
              AND flow_id=@flow AND flow_version=@version AND byte_length=@bytes AND modified_at=@modified
            """, connection, transaction);
        command.Parameters.AddWithValue("library", libraryId);
        command.Parameters.AddWithValue("path", key.GetProperty("relative_path").GetString()!);
        command.Parameters.AddWithValue("flow", Guid.Parse(key.GetProperty("flow_id").GetString()!));
        command.Parameters.AddWithValue("version", key.GetProperty("flow_version").GetInt32());
        command.Parameters.AddWithValue("bytes", key.GetProperty("byte_length").GetInt64());
        command.Parameters.AddWithValue("modified", ReadInstant(key.GetProperty("modified_at").GetString()!));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string JsonKind(JsonObject row) => row.ToJsonString(JsonOptions);

    private static async Task<DateTimeOffset?> ReadTableCursorAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT cursor_value FROM cluster_sync_table_cursors WHERE entity_type=@type", connection);
        command.Parameters.AddWithValue("type", table);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : ReadInstant(value);
    }

    private static async Task UpsertTableCursorAsync(DbConnection connection, DbTransaction transaction, string table, DateTimeOffset cursor, CancellationToken ct)
    {
        await using var command = Sql.Command("INSERT INTO cluster_sync_table_cursors(entity_type,cursor_value) VALUES(@type,@cursor) ON CONFLICT(entity_type) DO UPDATE SET cursor_value=EXCLUDED.cursor_value WHERE cluster_sync_table_cursors.cursor_value < EXCLUDED.cursor_value", connection, transaction);
        command.Parameters.AddWithValue("type", table);
        command.Parameters.AddWithValue("cursor", cursor);
        await command.ExecuteNonQueryAsync(ct);
    }

    private JsonObject PrepareRowForLocalStorage(EntityTable table, JsonObject row)
    {
        var local = (JsonObject)row.DeepClone();
        if ((table.Name is "plugin_configurations" or "flow_plugin_configurations" or "ai_credentials") && local["encrypted_values"] is { } values)
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes(values.ToJsonString(JsonOptions));
            local["encrypted_values"] = Convert.ToHexString(LocalProtector(table.Name).Protect(plaintext)).ToLowerInvariant();
        }
        return local;
    }

    private static byte[]? ReadHex(JsonNode? value)
    {
        if (value is null) return null;
        var text = value.GetValue<string>();
        return string.IsNullOrEmpty(text) ? [] : Convert.FromHexString(text);
    }

    private async Task DeleteRowAsync(DbConnection connection, DbTransaction transaction, EntityTable table, string keyJson, CancellationToken ct)
    {
        if (table.Name == "libraries")
        {
            // The owner already checked for active work; release local work rows that would block the delete.
            await Encoding.Api.Libraries.LibraryRepository.DeleteWithHistoryAsync(connection, transaction, Guid.Parse(JsonNode.Parse(keyJson)!["id"]!.GetValue<string>()), ct);
            return;
        }
        var sql = $"DELETE FROM {table.Name} WHERE {string.Join(" AND ", table.KeyColumns.Select(column => column + " = json_extract(@key, '$." + column + "')"))}";
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue("key", keyJson);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<LocalIdentity> ReadLocalIdentityAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT c.id,s.origin_id,s.next_sequence FROM cluster_identity c CROSS JOIN cluster_sync_identity s WHERE c.singleton=true AND s.singleton=true FOR UPDATE", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Identité du cluster ou du journal absente.");
        return new(ReadGuid(reader, 0), ReadGuid(reader, 1), Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture));
    }

    private async Task InsertLocalEventAsync(DbConnection connection, DbTransaction transaction, Guid clusterId, Guid originId, long sequence, string eventType, string payload, DateTimeOffset occurredAt, CancellationToken ct)
    {
        await using var command = Sql.Command("INSERT INTO cluster_change_log(event_id,cluster_id,origin_id,sequence,event_type,payload,occurred_at) VALUES(@id,@cluster,@origin,@sequence,@type,@payload,@time)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("cluster", clusterId);
        command.Parameters.AddWithValue("origin", originId);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("type", eventType);
        command.Parameters.AddWithValue("payload", SqlKind.Json, payload);
        command.Parameters.AddWithValue("time", occurredAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<Dictionary<string, EntityState>> ReadEntityStatesAsync(DbConnection connection, DbTransaction transaction, string type, CancellationToken ct, IReadOnlyCollection<string>? keys = null)
    {
        var states = new Dictionary<string, EntityState>(StringComparer.Ordinal);
        var filter = keys is null ? "" : " AND entity_key IN(SELECT value FROM json_each(@keys))";
        await using var command = Sql.Command("SELECT entity_key,fingerprint,occurred_at,origin_id,sequence,is_deleted FROM cluster_sync_entities WHERE entity_type=@type" + filter, connection, transaction);
        command.Parameters.AddWithValue("type", type);
        if (keys is not null) command.Parameters.AddWithValue("keys", JsonSerializer.Serialize(keys, JsonOptions));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) states[reader.GetString(0)] = new(reader.GetString(1), ReadInstant(reader.GetValue(2)), ReadGuid(reader, 3), Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture), Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture));
        return states;
    }

    private async Task<EntityState?> ReadEntityStateAsync(DbConnection connection, DbTransaction transaction, string type, string key, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT fingerprint,occurred_at,origin_id,sequence,is_deleted FROM cluster_sync_entities WHERE entity_type=@type AND entity_key=@key", connection, transaction);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), ReadInstant(reader.GetValue(1)), ReadGuid(reader, 2), Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture), Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture)) : null;
    }

    private async Task UpsertEntityStateAsync(DbConnection connection, DbTransaction transaction, string type, string key, string fingerprint, DateTimeOffset occurredAt, Guid originId, long sequence, bool deleted, CancellationToken ct)
    {
        await using var command = Sql.Command("INSERT INTO cluster_sync_entities(entity_type,entity_key,fingerprint,occurred_at,origin_id,sequence,is_deleted) VALUES(@type,@key,@fingerprint,@time,@origin,@sequence,@deleted) ON CONFLICT(entity_type,entity_key) DO UPDATE SET fingerprint=EXCLUDED.fingerprint,occurred_at=EXCLUDED.occurred_at,origin_id=EXCLUDED.origin_id,sequence=EXCLUDED.sequence,is_deleted=EXCLUDED.is_deleted", connection, transaction);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("time", occurredAt);
        command.Parameters.AddWithValue("origin", originId);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("deleted", deleted);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeletePendingAsync(DbConnection connection, DbTransaction transaction, Guid eventId, CancellationToken ct)
    {
        await using var command = Sql.Command("DELETE FROM cluster_sync_pending_projections WHERE event_id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", eventId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool TryGetTable(string name, out EntityTable table)
    {
        table = name == MediaTable.Name ? MediaTable
            : name == ProcessingReceiptTable.Name ? ProcessingReceiptTable
            : Tables.FirstOrDefault(candidate => candidate.Name == name)!;
        return table is not null;
    }

    private static EntityTable GetTable(string name) => TryGetTable(name, out var table) ? table : throw new InvalidOperationException("Type d'entité non pris en charge.");

    private static bool KeyBelongsToLibrary(string key, string libraryId)
    {
        try { return string.Equals(JsonDocument.Parse(key).RootElement.GetProperty("library_id").GetString(), libraryId, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static IEnumerable<string> SensitiveColumns(string table) => table switch
    {
        "cluster_nodes" => ["node_token_hash"],
        "node_enrollment_requests" => ["pairing_key_hash", "node_token_hash"],
        "users" => ["password_hash"],
        "plugin_configurations" or "flow_plugin_configurations" or "ai_credentials" => ["encrypted_values"],
        _ => []
    };

    private static int CompareVersion(DateTimeOffset leftTime, Guid leftOrigin, long leftSequence, DateTimeOffset rightTime, Guid rightOrigin, long rightSequence)
    {
        var time = leftTime.CompareTo(rightTime);
        if (time != 0) return time;
        if (leftOrigin == rightOrigin) return leftSequence.CompareTo(rightSequence);
        return string.CompareOrdinal(leftOrigin.ToString("N"), rightOrigin.ToString("N"));
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => reader.GetValue(ordinal) is Guid value ? value : Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);
    private static DateTimeOffset ReadInstant(object value) => value switch
    {
        DateTimeOffset instant => instant.ToUniversalTime(),
        DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
    };

    private sealed class EntityTable
    {
        public EntityTable(string name, string[] columns, string[] keyColumns, string[]? jsonColumns = null, string[]? arrayColumns = null, string[]? binaryColumns = null, string[]? fingerprintColumns = null, string[]? orderBy = null, string? filter = null, bool immutable = false, bool detectDeletes = true, string[]? preserveColumnsOnUpdate = null, string? deltaColumn = null)
        {
            Name = name; Columns = columns; KeyColumns = keyColumns; JsonColumns = jsonColumns ?? []; ArrayColumns = arrayColumns ?? [];
            BinaryColumns = binaryColumns ?? []; FingerprintColumns = fingerprintColumns; OrderBy = orderBy ?? []; Filter = filter; Immutable = immutable;
            DetectDeletes = detectDeletes; PreserveColumnsOnUpdate = preserveColumnsOnUpdate ?? []; DeltaColumn = deltaColumn;
        }
        public string Name { get; }
        public string[] Columns { get; }
        public string[] KeyColumns { get; }
        public string[] JsonColumns { get; }
        public string[] ArrayColumns { get; }
        public string[] BinaryColumns { get; }
        public string[]? FingerprintColumns { get; }
        public string[] OrderBy { get; }
        public string? Filter { get; }
        public bool Immutable { get; }
        public bool DetectDeletes { get; }
        public string[] PreserveColumnsOnUpdate { get; }
        public string? DeltaColumn { get; }
    }

    private sealed record EntitySnapshot(EntityTable Table, string Key, JsonObject Row, string Fingerprint, DateTimeOffset? CursorValue);
    private sealed record NodePresenceSnapshot(string Key, string LastSeenAt, string NodeId);
    private sealed record EntitySetEnvelope(string Table, JsonNode Key, string Fingerprint, DateTimeOffset OccurredAt, JsonObject Row);
    private sealed record EntityDeleteEnvelope(string Table, string Key);
    private sealed record NodePresenceEnvelope(string NodeId, string LastSeenAt);
    private sealed record EntityState(string Fingerprint, DateTimeOffset OccurredAt, Guid OriginId, long Sequence, bool IsDeleted);
    private sealed record LocalIdentity(Guid ClusterId, Guid OriginId, long NextSequence);
    private sealed record PendingChange(ClusterChangeEvent Change);
}
