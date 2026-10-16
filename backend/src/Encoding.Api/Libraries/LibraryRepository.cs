using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Libraries;
using Encoding.Api.Infrastructure.PeerCluster;
using System.Text.Json;

namespace Encoding.Api.Libraries;

public sealed class LibraryRepository(DbDataSource dataSource, PeerConsensusService? peerConsensus = null)
{
    public async Task<IReadOnlyList<LibraryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT l.id, l.name, l.created_at,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode <> 'unsupported')::integer,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'direct')::integer,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'transfer')::integer,
                   l.is_active,
                   (SELECT v.definition->>'name' FROM library_flow_assignments f JOIN flow_versions v ON v.flow_id=f.flow_id AND v.version=f.flow_version WHERE f.library_id=l.id),
                   (SELECT f.flow_version FROM library_flow_assignments f WHERE f.library_id=l.id)
            FROM libraries l
            LEFT JOIN library_node_accesses a ON a.library_id = l.id
            WHERE NOT l.is_deleted
            GROUP BY l.id
            ORDER BY l.name, l.id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<LibraryResponse>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<LibraryResponse?> CreateAsync(CreateLibraryRequest request, CancellationToken cancellationToken)
    {
        if (peerConsensus is not null)
        {
            var name = request.Name.Trim();
            var id = Guid.NewGuid();
            var state = new PeerLibraryState(id, name, DateTimeOffset.UtcNow, true);
            return await peerConsensus.ReplicateLibraryAsync(state, id, 0, false, cancellationToken)
                ? await GetAsync(id, cancellationToken) : null;
        }

        const string sql = """
            INSERT INTO libraries (name) VALUES (@name)
            ON CONFLICT (name) DO NOTHING
            RETURNING id, name, created_at, 0::integer, 0::integer, 0::integer, is_active
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<LibraryResponse?> UpdateAsync(Guid id, UpdateLibraryRequest request, CancellationToken cancellationToken)
    {
        if (peerConsensus is not null)
        {
            var current = peerConsensus.GetLibraryDocument(id);
            if (current is null || current.Deleted) return null;
            var library = current.Value.Deserialize<PeerLibraryState>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (library is null) return null;
            var updated = library with { Name = request.Name.Trim() };
            return await peerConsensus.ReplicateLibraryAsync(updated, id, current.Revision, false, cancellationToken)
                ? await GetAsync(id, cancellationToken) : null;
        }

        const string sql = """
            UPDATE libraries SET name = @name
            WHERE id = @id
            RETURNING id, name, created_at, 0::integer, 0::integer, 0::integer, is_active
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        await reader.CloseAsync();
        return await GetAsync(id, cancellationToken);
    }

    public async Task<LibraryResponse?> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        if (peerConsensus is not null)
        {
            var current = peerConsensus.GetLibraryDocument(id);
            if (current is null || current.Deleted) return null;
            var library = current.Value.Deserialize<PeerLibraryState>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (library is null) return null;
            var updated = library with { IsActive = isActive };
            return await peerConsensus.ReplicateLibraryAsync(updated, id, current.Revision, false, cancellationToken)
                ? await GetAsync(id, cancellationToken) : null;
        }

        const string sql = "UPDATE libraries SET is_active = @active WHERE id = @id";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("active", isActive);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        return await GetAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (peerConsensus is not null)
        {
            var current = peerConsensus.GetLibraryDocument(id);
            if (current is null || current.Deleted) return false;
            return await peerConsensus.ReplicateLibraryAsync(null, id, current.Revision, true, cancellationToken);
        }

        const string sql = "DELETE FROM libraries WHERE id = @id";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<LibraryResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT l.id, l.name, l.created_at,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode <> 'unsupported')::integer,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'direct')::integer,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'transfer')::integer,
                   l.is_active,
                   (SELECT v.definition->>'name' FROM library_flow_assignments f JOIN flow_versions v ON v.flow_id=f.flow_id AND v.version=f.flow_version WHERE f.library_id=l.id),
                   (SELECT f.flow_version FROM library_flow_assignments f WHERE f.library_id=l.id)
            FROM libraries l LEFT JOIN library_node_accesses a ON a.library_id = l.id
            WHERE l.id = @id AND NOT l.is_deleted GROUP BY l.id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static LibraryResponse Read(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
        reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6), reader.FieldCount>7 && !reader.IsDBNull(7)?reader.GetString(7):null, reader.FieldCount>8 && !reader.IsDBNull(8)?reader.GetInt32(8):null);
}
