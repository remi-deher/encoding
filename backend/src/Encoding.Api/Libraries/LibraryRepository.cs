using Encoding.Contracts.Libraries;
using Npgsql;

namespace Encoding.Api.Libraries;

public sealed class LibraryRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<LibraryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT l.id, l.name, l.created_at,
                   count(a.node_id) FILTER (WHERE a.mode <> 'unsupported')::integer,
                   count(a.node_id) FILTER (WHERE a.mode = 'direct')::integer,
                   count(a.node_id) FILTER (WHERE a.mode = 'transfer')::integer,
                   l.is_active
            FROM libraries l
            LEFT JOIN library_node_accesses a ON a.library_id = l.id
            GROUP BY l.id
            ORDER BY l.name, l.id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<LibraryResponse>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<LibraryResponse?> CreateAsync(CreateLibraryRequest request, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO libraries (name) VALUES (@name)
            ON CONFLICT (name) DO NOTHING
            RETURNING id, name, created_at, 0::integer, 0::integer, 0::integer, is_active
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<LibraryResponse?> UpdateAsync(Guid id, UpdateLibraryRequest request, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE libraries SET name = @name
            WHERE id = @id
            RETURNING id, name, created_at, 0::integer, 0::integer, 0::integer, is_active
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        await reader.CloseAsync();
        return await GetAsync(id, cancellationToken);
    }

    public async Task<LibraryResponse?> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE libraries SET is_active = @active WHERE id = @id";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("active", isActive);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        return await GetAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM libraries WHERE id = @id";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<LibraryResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT l.id, l.name, l.created_at,
                   count(a.node_id) FILTER (WHERE a.mode <> 'unsupported')::integer,
                   count(a.node_id) FILTER (WHERE a.mode = 'direct')::integer,
                   count(a.node_id) FILTER (WHERE a.mode = 'transfer')::integer,
                   l.is_active
            FROM libraries l LEFT JOIN library_node_accesses a ON a.library_id = l.id
            WHERE l.id = @id GROUP BY l.id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static LibraryResponse Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
        reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6));
}
