using Encoding.Contracts.Libraries;
using Npgsql;

namespace Encoding.Api.Libraries;

public sealed class LibraryRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<LibraryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT id, name, media_type, created_at FROM libraries ORDER BY name, id";
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
            INSERT INTO libraries (name, media_type) VALUES (@name, @media_type)
            ON CONFLICT (name) DO NOTHING
            RETURNING id, name, media_type, created_at
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("media_type", request.MediaType.ToString().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static LibraryResponse Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), Enum.Parse<LibraryMediaType>(reader.GetString(2), true),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
}
