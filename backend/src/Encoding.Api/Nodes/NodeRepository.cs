using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Encoding.Api.Nodes;

public sealed class NodeRepository(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<NodeResponse?> EnrollAsync(NodeEnrollmentRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.NodeToken));
        const string sql = """
            INSERT INTO cluster_nodes (id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, node_token_hash)
            VALUES (@id, @name, @roles, @max_concurrent_tasks, @capabilities, clock_timestamp(), @node_token_hash)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                last_seen_at = clock_timestamp()
            WHERE cluster_nodes.deleted_at IS NULL AND (cluster_nodes.node_token_hash IS NULL OR cluster_nodes.node_token_hash = EXCLUDED.node_token_hash)
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddNodeParameters(command, request);
        command.Parameters.AddWithValue("node_token_hash", NpgsqlDbType.Bytea, tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, true) : null;
    }

    public async Task<bool> CreatePairingRequestAsync(CreateNodeEnrollmentRequest request, CancellationToken cancellationToken)
    {
        await using var deleted=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@id AND deleted_at IS NOT NULL)");
        deleted.Parameters.AddWithValue("id",request.Node.Id); if((bool)(await deleted.ExecuteScalarAsync(cancellationToken))!) return false;
        var pairingKeyHash = HashSecret(request.PairingKey);
        var nodeTokenHash = HashSecret(request.Node.NodeToken);
        const string sql = """
            INSERT INTO node_enrollment_requests
                (node_id, pairing_key_hash, node_token_hash, name, roles, max_concurrent_tasks, capabilities, expires_at)
            VALUES
                (@id, @pairing_key_hash, @node_token_hash, @name, @roles, @max_concurrent_tasks, @capabilities, clock_timestamp() + interval '10 minutes')
            ON CONFLICT (node_id) DO UPDATE SET
                pairing_key_hash = EXCLUDED.pairing_key_hash,
                node_token_hash = EXCLUDED.node_token_hash,
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
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
            request.Node.Capabilities));
        command.Parameters.AddWithValue("pairing_key_hash", NpgsqlDbType.Bytea, pairingKeyHash);
        command.Parameters.AddWithValue("node_token_hash", NpgsqlDbType.Bytea, nodeTokenHash);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<PendingNodeEnrollmentResponse>> ListPendingPairingRequestsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT node_id, name, roles, max_concurrent_tasks, capabilities, created_at, expires_at
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
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc))));
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
            SELECT node_id, node_token_hash, name, roles, max_concurrent_tasks, capabilities
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
                JsonSerializer.Deserialize<IReadOnlyList<NodeToolCapability>>(reader.GetString(5), JsonOptions) ?? []);
        }

        const string enrollSql = """
            INSERT INTO cluster_nodes (id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, node_token_hash)
            VALUES (@id, @name, @roles, @max_concurrent_tasks, @capabilities, clock_timestamp(), @node_token_hash)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                last_seen_at = clock_timestamp()
            WHERE cluster_nodes.deleted_at IS NULL AND (cluster_nodes.node_token_hash IS NULL OR cluster_nodes.node_token_hash = EXCLUDED.node_token_hash)
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at
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
                last_seen_at = clock_timestamp()
            WHERE id = @id AND deleted_at IS NULL AND node_token_hash = @node_token_hash
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at
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
                   last_seen_at >= clock_timestamp() - @offline_after AS is_online
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
                   last_seen_at >= clock_timestamp() - @offline_after AS is_online
            FROM cluster_nodes WHERE id = @id AND deleted_at IS NULL
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("offline_after", offlineAfter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, reader.GetBoolean(6)) : null;
    }


    public async Task<string> DeleteAsync(Guid id,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using var node=new NpgsqlCommand("SELECT id FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL FOR UPDATE",c,tx);
        node.Parameters.AddWithValue("id",id); if(await node.ExecuteScalarAsync(ct) is null) return "missing";
        await using var active=new NpgsqlCommand("""
          SELECT EXISTS(SELECT 1 FROM execution_work w JOIN executions e ON e.id=w.execution_id
            WHERE (w.worker_node_id=@id OR w.source_node_id=@id) AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling'))
          OR EXISTS(SELECT 1 FROM library_scans WHERE node_id=@id AND state='running' AND lease_until>clock_timestamp())
          """,c,tx);
        active.Parameters.AddWithValue("id",id); if((bool)(await active.ExecuteScalarAsync(ct))!) return "busy";
        await using var remove=new NpgsqlCommand("""
          UPDATE cluster_nodes SET deleted_at=clock_timestamp(),node_token_hash=NULL WHERE id=@id;
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
        AddNodeParameters(command, request.Id, new NodeHeartbeatRequest(request.Name, request.Roles, request.MaxConcurrentTasks, request.Capabilities));
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
    }

    private static NodeResponse ReadResponse(NpgsqlDataReader reader, bool isOnline)
    {
        var lastSeenAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc));
        var roles = reader.GetFieldValue<string[]>(2).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray();
        var capabilities = JsonSerializer.Deserialize<IReadOnlyList<NodeToolCapability>>(reader.GetString(4), JsonOptions) ?? [];
        return new NodeResponse(reader.GetGuid(0), reader.GetString(1), roles, reader.GetInt32(3), capabilities,
            isOnline ? NodePresenceStatus.Online : NodePresenceStatus.Offline, lastSeenAt);
    }

    private static byte[] HashSecret(string value) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
}
