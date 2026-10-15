using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Encoding.Api.Nodes;

public enum PairingRequestResult { Created, Conflict, TooMany }

public sealed class NodeRepository(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Enrôle un nœud. Une nouvelle identité n’est acceptée que fenêtre ouverte ; une identité déjà connue
    /// avec le même secret peut répéter l’appel (reprise après interruption).
    /// </summary>
    public async Task<NodeResponse?> EnrollAsync(NodeEnrollmentRequest request, bool allowNewNode, CancellationToken cancellationToken)
    {
        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.NodeToken));
        const string sql = """
            INSERT INTO cluster_nodes (id, name, roles, max_concurrent_tasks, capabilities, inventory, last_seen_at, node_token_hash)
            SELECT @id, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, clock_timestamp(), @node_token_hash
            WHERE @allow_new OR EXISTS (SELECT 1 FROM cluster_nodes WHERE id = @id AND deleted_at IS NULL AND node_token_hash = @node_token_hash)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                last_seen_at = clock_timestamp()
            WHERE cluster_nodes.deleted_at IS NULL AND (cluster_nodes.node_token_hash IS NULL OR cluster_nodes.node_token_hash = EXCLUDED.node_token_hash)
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, inventory, display_name, labels
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddNodeParameters(command, request);
        command.Parameters.AddWithValue("node_token_hash", NpgsqlDbType.Bytea, tokenHash);
        command.Parameters.AddWithValue("allow_new", allowNewNode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, true) : null;
    }

    /// <summary>Authentifie un worker par son seul secret ; l’empreinte SHA-256 de ce secret aléatoire à 256 bits identifie le nœud.</summary>
    public async Task<bool> AuthenticateTokenAsync(string token, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE node_token_hash = @hash AND deleted_at IS NULL)");
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, HashSecret(token));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<PairingRequestResult> CreatePairingRequestAsync(CreateNodeEnrollmentRequest request, int maxPending, CancellationToken cancellationToken)
    {
        await using var deleted=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@id AND deleted_at IS NOT NULL)");
        deleted.Parameters.AddWithValue("id",request.Node.Id); if((bool)(await deleted.ExecuteScalarAsync(cancellationToken))!) return PairingRequestResult.Conflict;
        // Borne les demandes en attente pour qu’un appelant anonyme ne puisse pas saturer la liste d’approbation.
        await using var pending=dataSource.CreateCommand("SELECT count(*) FROM node_enrollment_requests WHERE expires_at > clock_timestamp() AND node_id <> @id");
        pending.Parameters.AddWithValue("id",request.Node.Id); if((long)(await pending.ExecuteScalarAsync(cancellationToken))! >= maxPending) return PairingRequestResult.TooMany;
        var pairingKeyHash = HashSecret(request.PairingKey);
        var nodeTokenHash = HashSecret(request.Node.NodeToken);
        const string sql = """
            INSERT INTO node_enrollment_requests
                (node_id, pairing_key_hash, node_token_hash, name, roles, max_concurrent_tasks, capabilities, inventory, expires_at)
            VALUES
                (@id, @pairing_key_hash, @node_token_hash, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, clock_timestamp() + interval '10 minutes')
            ON CONFLICT (node_id) DO UPDATE SET
                pairing_key_hash = EXCLUDED.pairing_key_hash,
                node_token_hash = EXCLUDED.node_token_hash,
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                created_at = clock_timestamp(),
                expires_at = EXCLUDED.expires_at
            WHERE node_enrollment_requests.node_token_hash = EXCLUDED.node_token_hash
               OR node_enrollment_requests.expires_at <= clock_timestamp()
            RETURNING node_id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddNodeParameters(command, request.Node.Id, new NodeHeartbeatRequest(
            request.Node.Name,
            request.Node.Roles,
            request.Node.MaxConcurrentTasks,
            request.Node.Capabilities,
            request.Node.Inventory));
        command.Parameters.AddWithValue("pairing_key_hash", NpgsqlDbType.Bytea, pairingKeyHash);
        command.Parameters.AddWithValue("node_token_hash", NpgsqlDbType.Bytea, nodeTokenHash);
        return await command.ExecuteScalarAsync(cancellationToken) is not null ? PairingRequestResult.Created : PairingRequestResult.Conflict;
    }

    public async Task<IReadOnlyList<PendingNodeEnrollmentResponse>> ListPendingPairingRequestsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT node_id, name, roles, max_concurrent_tasks, capabilities, created_at, expires_at, inventory
            FROM node_enrollment_requests
            WHERE expires_at > clock_timestamp() AND NOT EXISTS(SELECT 1 FROM cluster_nodes n WHERE n.id=node_enrollment_requests.node_id AND n.deleted_at IS NOT NULL)
            ORDER BY created_at, node_id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var requests = new List<PendingNodeEnrollmentResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var roles = reader.GetFieldValue<string[]>(2).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray();
            var capabilities = JsonSerializer.Deserialize<IReadOnlyList<NodeToolCapability>>(reader.GetString(4), JsonOptions) ?? [];
            requests.Add(new PendingNodeEnrollmentResponse(
                reader.GetGuid(0), reader.GetString(1), roles, reader.GetInt32(3), capabilities,
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                ReadInventory(reader.GetString(7))));
        }
        return requests;
    }

    public async Task<NodeResponse?> ApprovePairingRequestAsync(string pairingKey, CancellationToken cancellationToken)
    {
        return await ApprovePairingRequestAsync(HashSecret(pairingKey), null, cancellationToken);
    }

    public async Task<NodeResponse?> ApprovePairingRequestAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return await ApprovePairingRequestAsync(null, nodeId, cancellationToken);
    }

    private async Task<NodeResponse?> ApprovePairingRequestAsync(byte[]? pairingKeyHash, Guid? requestedNodeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var requestPredicate = requestedNodeId.HasValue ? "node_id = @node_id" : "pairing_key_hash = @pairing_key_hash";
        var selectSql = $"""
            SELECT node_id, node_token_hash, name, roles, max_concurrent_tasks, capabilities, inventory
            FROM node_enrollment_requests
            WHERE {requestPredicate} AND expires_at > clock_timestamp()
            FOR UPDATE
            """;
        Guid nodeId;
        byte[] nodeTokenHash;
        NodeHeartbeatRequest heartbeat;
        await using (var selectCommand = new NpgsqlCommand(selectSql, connection, transaction))
        {
            if (requestedNodeId.HasValue) selectCommand.Parameters.AddWithValue("node_id", requestedNodeId.Value);
            else selectCommand.Parameters.AddWithValue("pairing_key_hash", NpgsqlDbType.Bytea, pairingKeyHash!);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            nodeId = reader.GetGuid(0);
            nodeTokenHash = reader.GetFieldValue<byte[]>(1);
            heartbeat = new NodeHeartbeatRequest(
                reader.GetString(2),
                reader.GetFieldValue<string[]>(3).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray(),
                reader.GetInt32(4),
                JsonSerializer.Deserialize<IReadOnlyList<NodeToolCapability>>(reader.GetString(5), JsonOptions) ?? [],
                ReadInventory(reader.GetString(6)));
        }

        const string enrollSql = """
            INSERT INTO cluster_nodes (id, name, roles, max_concurrent_tasks, capabilities, inventory, last_seen_at, node_token_hash)
            VALUES (@id, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, clock_timestamp(), @node_token_hash)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                last_seen_at = clock_timestamp()
            WHERE cluster_nodes.deleted_at IS NULL AND (cluster_nodes.node_token_hash IS NULL OR cluster_nodes.node_token_hash = EXCLUDED.node_token_hash)
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, inventory, display_name, labels
            """;
        NodeResponse? node;
        await using (var enrollCommand = new NpgsqlCommand(enrollSql, connection, transaction))
        {
            AddNodeParameters(enrollCommand, nodeId, heartbeat);
            enrollCommand.Parameters.AddWithValue("node_token_hash", NpgsqlDbType.Bytea, nodeTokenHash);
            await using var reader = await enrollCommand.ExecuteReaderAsync(cancellationToken);
            node = await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, true) : null;
        }
        if (node is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var deleteCommand = new NpgsqlCommand(
            "DELETE FROM node_enrollment_requests WHERE node_id = @id", connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("id", nodeId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return node;
    }

    public async Task<NodeResponse?> RecordHeartbeatAsync(Guid nodeId, string token, NodeHeartbeatRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        const string sql = """
            UPDATE cluster_nodes SET
                name = @name,
                roles = @roles,
                max_concurrent_tasks = @max_concurrent_tasks,
                capabilities = @capabilities,
                inventory = @inventory,
                desired_roles = CASE WHEN desired_roles <@ @roles AND @roles <@ desired_roles THEN NULL ELSE desired_roles END,
                last_seen_at = clock_timestamp()
            WHERE id = @id AND deleted_at IS NULL AND node_token_hash = @node_token_hash
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, true AS is_online, inventory, display_name, labels, desired_roles
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddNodeParameters(command, nodeId, request);
        command.Parameters.AddWithValue("node_token_hash", NpgsqlDbType.Bytea, tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, true) : null;
    }

    public async Task<IReadOnlyList<NodeResponse>> ListAsync(TimeSpan offlineAfter, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, roles, max_concurrent_tasks, capabilities, last_seen_at,
                   last_seen_at >= clock_timestamp() - @offline_after AS is_online, inventory, display_name, labels, desired_roles
            FROM cluster_nodes WHERE deleted_at IS NULL
            ORDER BY name, id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("offline_after", offlineAfter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = new List<NodeResponse>();
        while (await reader.ReadAsync(cancellationToken)) nodes.Add(ReadResponse(reader, reader.GetBoolean(6)));
        return nodes;
    }

    public async Task<NodeResponse?> GetAsync(Guid nodeId, TimeSpan offlineAfter, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, roles, max_concurrent_tasks, capabilities, last_seen_at,
                   last_seen_at >= clock_timestamp() - @offline_after AS is_online, inventory, display_name, labels, desired_roles
            FROM cluster_nodes WHERE id = @id AND deleted_at IS NULL
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("offline_after", offlineAfter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, reader.GetBoolean(6)) : null;
    }


    public enum ModeRequestResult { Accepted, NotFound, LastServer }

    /// <summary>
    /// Enregistre le mode voulu d'un nœud ; le nœud l'applique à son prochain signal de présence. Refuse de retirer la fonction
    /// serveur du dernier nœud serveur en ligne (les changements de mode sont sérialisés par un verrou consultatif).
    /// </summary>
    public async Task<ModeRequestResult> RequestModeAsync(Guid nodeId, bool server, bool worker, TimeSpan offlineAfter, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('cluster_node_mode'))", c, tx))
            await lockCommand.ExecuteNonQueryAsync(ct);
        string[] roles;
        await using (var select = new NpgsqlCommand("SELECT roles FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL FOR UPDATE", c, tx))
        {
            select.Parameters.AddWithValue("id", nodeId);
            if (await select.ExecuteScalarAsync(ct) is not string[] current) return ModeRequestResult.NotFound;
            roles = current;
        }
        if (roles.Contains("server") && !server)
        {
            await using var others = new NpgsqlCommand("""
                SELECT count(*) FROM cluster_nodes
                WHERE id <> @id AND deleted_at IS NULL AND 'server' = ANY(roles) AND 'server' = ANY(COALESCE(desired_roles, roles))
                  AND last_seen_at >= clock_timestamp() - @offline_after
                """, c, tx);
            others.Parameters.AddWithValue("id", nodeId);
            others.Parameters.AddWithValue("offline_after", offlineAfter);
            if ((long)(await others.ExecuteScalarAsync(ct))! == 0) return ModeRequestResult.LastServer;
        }
        var desired = new[] { server ? "server" : null, worker ? "worker" : null }.Where(role => role is not null).ToArray();
        await using (var update = new NpgsqlCommand("""
            UPDATE cluster_nodes SET desired_roles = CASE WHEN @desired <@ roles AND roles <@ @desired THEN NULL ELSE @desired END
            WHERE id = @id
            """, c, tx))
        {
            update.Parameters.AddWithValue("id", nodeId);
            update.Parameters.Add(new NpgsqlParameter("desired", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = desired });
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return ModeRequestResult.Accepted;
    }

    public async Task<string> DeleteAsync(Guid id,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using var node=new NpgsqlCommand("SELECT id FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL FOR UPDATE",c,tx);
        node.Parameters.AddWithValue("id",id); if(await node.ExecuteScalarAsync(ct) is null) return "missing";
        await using var active=new NpgsqlCommand("""
          SELECT EXISTS(SELECT 1 FROM execution_work w JOIN executions e ON e.id=w.execution_id
            WHERE (w.worker_node_id=@id OR w.source_node_id=@id) AND (w.lease_until>clock_timestamp() OR w.handoff_pending) AND e.state IN ('running','cancelling'))
          OR EXISTS(SELECT 1 FROM library_scans WHERE node_id=@id AND state='running' AND lease_until>clock_timestamp())
          """,c,tx);
        active.Parameters.AddWithValue("id",id); if((bool)(await active.ExecuteScalarAsync(ct))!) return "busy";
        await using var remove=new NpgsqlCommand("""
          UPDATE cluster_nodes SET deleted_at=clock_timestamp(),node_token_hash=NULL WHERE id=@id;
          DELETE FROM node_group_members WHERE node_id=@id;
          DELETE FROM library_node_accesses WHERE node_id=@id;
          UPDATE library_node_accesses SET is_enabled=false WHERE transfer_source_node_id=@id;
          DELETE FROM node_enrollment_requests WHERE node_id=@id;
          """,c,tx);
        remove.Parameters.AddWithValue("id",id); await remove.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return "deleted";
    }

    public async Task<NodeActivityResponse> ActivityAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("""
          SELECT
           (SELECT count(*)::integer FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.worker_node_id=@id AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')),
           (SELECT count(*)::integer FROM executions WHERE node_id=@id AND state='succeeded'),
           (SELECT count(*)::integer FROM executions WHERE node_id=@id AND state='failed'),
           (SELECT count(*)::integer FROM executions WHERE node_id=@id AND state='cancelled'),
           (SELECT max(updated_at) FROM executions WHERE node_id=@id),
           (SELECT count(*)::integer FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.source_node_id=@id AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling'))
          """);
        cmd.Parameters.AddWithValue("id",id); await using var r=await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        return new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.IsDBNull(4)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(4),r.GetInt32(5));
    }

    private static void AddNodeParameters(NpgsqlCommand command, NodeEnrollmentRequest request)
    {
        AddNodeParameters(command, request.Id, new NodeHeartbeatRequest(request.Name, request.Roles, request.MaxConcurrentTasks, request.Capabilities, request.Inventory));
    }

    private static void AddNodeParameters(NpgsqlCommand command, Guid nodeId, NodeHeartbeatRequest request)
    {
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("roles", request.Roles.Select(role => role.ToString().ToLowerInvariant()).ToArray());
        command.Parameters.AddWithValue("max_concurrent_tasks", request.MaxConcurrentTasks);
        command.Parameters.Add(new NpgsqlParameter("capabilities", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(request.Capabilities, JsonOptions)
        });
        command.Parameters.Add(new NpgsqlParameter("inventory", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(request.Inventory ?? new NodeInventory("unknown", "unknown", 0, 0), JsonOptions)
        });
    }

    internal static NodeResponse ReadResponse(NpgsqlDataReader reader, bool isOnline)
    {
        var lastSeenAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc));
        var roles = reader.GetFieldValue<string[]>(2).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray();
        var capabilities = JsonSerializer.Deserialize<IReadOnlyList<NodeToolCapability>>(reader.GetString(4), JsonOptions) ?? [];
        var inventoryOrdinal = reader.FieldCount >= 10 ? 7 : 6;
        var inventory = ReadInventory(reader.GetString(inventoryOrdinal));
        var displayName = reader.FieldCount > inventoryOrdinal + 1 && !reader.IsDBNull(inventoryOrdinal + 1) ? reader.GetString(inventoryOrdinal + 1) : null;
        var labels = reader.FieldCount > inventoryOrdinal + 2
            ? JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(inventoryOrdinal + 2), JsonOptions) ?? []
            : new Dictionary<string, string?>();
        var desiredOrdinal = inventoryOrdinal + 3;
        var desired = reader.FieldCount > desiredOrdinal && !reader.IsDBNull(desiredOrdinal)
            ? reader.GetFieldValue<string[]>(desiredOrdinal).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray()
            : null;
        return new NodeResponse(reader.GetGuid(0), reader.GetString(1), roles, reader.GetInt32(3), capabilities,
            isOnline ? NodePresenceStatus.Online : NodePresenceStatus.Offline, lastSeenAt, inventory, displayName, labels, desired);
    }

    private static byte[] HashSecret(string value) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));

    private static NodeInventory? ReadInventory(string json) => json == "{}" ? null : JsonSerializer.Deserialize<NodeInventory>(json, JsonOptions);
}
