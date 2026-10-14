using System.Text.Json;
using Encoding.Contracts.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Encoding.Api.Nodes;

public sealed class NodeGroupRepository(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<NodeGroupResponse>> ListAsync(CancellationToken ct)
    {
        var groups = new List<GroupRow>();
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT id,name,description,mode,criteria,labels,created_at,updated_at FROM node_groups ORDER BY name,id", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) groups.Add(ReadGroup(r));
        var manualMembers = new Dictionary<Guid, List<Guid>>();
        await using (var cmd = new NpgsqlCommand("SELECT group_id,node_id FROM node_group_members ORDER BY node_id", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!manualMembers.TryGetValue(r.GetGuid(0), out var ids)) manualMembers[r.GetGuid(0)] = ids = [];
                ids.Add(r.GetGuid(1));
            }
        var nodes = new List<NodeResponse>();
        const string nodeSql = "SELECT id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,true,inventory,display_name,labels FROM cluster_nodes WHERE deleted_at IS NULL";
        await using (var cmd = new NpgsqlCommand(nodeSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) nodes.Add(NodeRepository.ReadResponse(r, true));
        return groups.Select(g => new NodeGroupResponse(g.Id, g.Name, g.Description, g.Mode, g.Criteria, g.Labels,
            g.Mode == NodeGroupMode.Manual
                ? manualMembers.GetValueOrDefault(g.Id, [])
                : nodes.Where(n => Matches(g.Criteria, n)).Select(n => n.Id).ToArray(), g.CreatedAt, g.UpdatedAt)).ToArray();
    }

    public async Task<NodeGroupResponse?> GetAsync(Guid id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(g => g.Id == id);

    public async Task<bool> SaveAsync(Guid? id, SaveNodeGroupRequest request, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var groupId = id ?? Guid.NewGuid();
        var upsert = id.HasValue ? """
            UPDATE node_groups SET name=@name,description=@description,mode=@mode,criteria=@criteria,labels=@labels,updated_at=clock_timestamp()
            WHERE id=@id RETURNING id
            """ : """
            INSERT INTO node_groups(id,name,description,mode,criteria,labels)
            VALUES(@id,@name,@description,@mode,@criteria,@labels)
            RETURNING id
            """;
        await using (var cmd = new NpgsqlCommand(upsert, c, tx))
        {
            cmd.Parameters.AddWithValue("id", groupId);
            cmd.Parameters.AddWithValue("name", request.Name.Trim());
            cmd.Parameters.AddWithValue("description", request.Description.Trim());
            cmd.Parameters.AddWithValue("mode", request.Mode.ToString().ToLowerInvariant());
            cmd.Parameters.Add(new("criteria", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(request.Criteria, Json) });
            cmd.Parameters.Add(new("labels", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(request.Labels, Json) });
            if (await cmd.ExecuteScalarAsync(ct) is null) return false;
        }
        if (request.Mode == NodeGroupMode.Manual)
        {
            await using (var clear = new NpgsqlCommand("DELETE FROM node_group_members WHERE group_id=@id", c, tx))
            { clear.Parameters.AddWithValue("id", groupId); await clear.ExecuteNonQueryAsync(ct); }
            foreach (var nodeId in request.NodeIds.Distinct())
            {
                await using var add = new NpgsqlCommand("INSERT INTO node_group_members(group_id,node_id) SELECT @group,@node WHERE EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@node AND deleted_at IS NULL)", c, tx);
                add.Parameters.AddWithValue("group", groupId); add.Parameters.AddWithValue("node", nodeId);
                if (await add.ExecuteNonQueryAsync(ct) == 0) return false;
            }
        }
        else
        {
            await using var clear = new NpgsqlCommand("DELETE FROM node_group_members WHERE group_id=@id", c, tx);
            clear.Parameters.AddWithValue("id", groupId); await clear.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM node_groups WHERE id=@id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<NodeIdentityResponse?> GetNodeIdentityAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        string? displayName; IReadOnlyDictionary<string, string?> local;
        await using (var cmd = new NpgsqlCommand("SELECT display_name,labels FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL", c))
        {
            cmd.Parameters.AddWithValue("id", id); await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            displayName = r.IsDBNull(0) ? null : r.GetString(0);
            local = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(1), Json) ?? [];
        }
        var inherited = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ids = new List<Guid>();
        const string sql = """
            SELECT g.id,g.labels FROM node_groups g LEFT JOIN node_group_members m ON m.group_id=g.id
            WHERE m.node_id=@node ORDER BY g.name,g.id
            """;
        await using (var cmd = new NpgsqlCommand(sql, c))
        {
            cmd.Parameters.AddWithValue("node", id); await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetGuid(0));
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(1), Json) ?? []) inherited[key] = value;
            }
        }
        // Automatic groups are evaluated from the current node inventory at read/assignment time.
        foreach (var group in await ListAsync(ct))
            if (group.Mode == NodeGroupMode.Automatic && group.NodeIds.Contains(id))
            {
                ids.Add(group.Id);
                foreach (var (key, value) in group.Labels) inherited[key] = value;
            }
        foreach (var (key, value) in local) inherited[key] = value;
        return new(id, displayName, local, inherited, ids.Distinct().ToArray());
    }

    public async Task<bool> SaveNodeIdentityAsync(Guid id, SaveNodeIdentityRequest request, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("UPDATE cluster_nodes SET display_name=@name,labels=@labels WHERE id=@id AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", string.IsNullOrWhiteSpace(request.DisplayName) ? DBNull.Value : request.DisplayName.Trim());
        cmd.Parameters.Add(new("labels", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(request.Labels, Json) });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public static bool Matches(NodeGroupCriteria c, NodeResponse n)
    {
        var inv = n.Inventory;
        return (c.Roles is null || c.Roles.Count == 0 || c.Roles.Any(n.Roles.Contains))
            && (c.OperatingSystem is null || string.Equals(c.OperatingSystem, inv?.OperatingSystem, StringComparison.OrdinalIgnoreCase))
            && (c.Architecture is null || string.Equals(c.Architecture, inv?.Architecture, StringComparison.OrdinalIgnoreCase))
            && (c.MinimumMemoryMb is null || inv?.MemoryMb >= c.MinimumMemoryMb)
            && (c.MinimumProcessorCount is null || inv?.ProcessorCount >= c.MinimumProcessorCount)
            && (c.MinimumConcurrentTasks is null || n.MaxConcurrentTasks >= c.MinimumConcurrentTasks)
            && (c.RequiredToolIds is null || c.RequiredToolIds.All(tool => n.Capabilities.Any(x => string.Equals(x.ToolId, tool, StringComparison.OrdinalIgnoreCase))));
    }

    private static GroupRow ReadGroup(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2),
        Enum.Parse<NodeGroupMode>(r.GetString(3), true), JsonSerializer.Deserialize<NodeGroupCriteria>(r.GetString(4), Json) ?? new(),
        JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(5), Json) ?? [],
        new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc)));

    private sealed record GroupRow(Guid Id,string Name,string Description,NodeGroupMode Mode,NodeGroupCriteria Criteria,
        IReadOnlyDictionary<string,string?> Labels,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);
}
