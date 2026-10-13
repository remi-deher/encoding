using System.Text.Json;
using Encoding.Contracts.Flows;
using Npgsql;
using NpgsqlTypes;

namespace Encoding.Api.Flows;

public sealed class FlowRepository(NpgsqlDataSource dataSource)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT f.id,v.definition->>'name',v.definition->>'mediaType',v.version,
                   jsonb_array_length(v.definition->'nodes'),b.status,f.updated_at
            FROM flows f JOIN flow_versions v ON v.flow_id=f.id AND v.version=f.current_version
            JOIN flow_backup_jobs b ON b.flow_id=v.flow_id AND b.version=v.version
            ORDER BY f.updated_at DESC,f.id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<FlowSummary>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetString(5),reader.GetFieldValue<DateTimeOffset>(6)));
        return result;
    }

    public async Task<FlowVersionResponse?> GetAsync(Guid id, int? version, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT v.definition::text,b.status,v.created_at FROM flows f
            JOIN flow_versions v ON v.flow_id=f.id AND v.version=COALESCE(@version,f.current_version)
            JOIN flow_backup_jobs b ON b.flow_id=v.flow_id AND b.version=v.version WHERE f.id=@id
            """);
        command.Parameters.AddWithValue("id",id);
        command.Parameters.AddWithValue("version",NpgsqlDbType.Integer,(object?)version ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),JsonOptions)!,reader.GetString(1),reader.GetFieldValue<DateTimeOffset>(2)) : null;
    }

    /// <summary>CAS publication and backup outbox share one transaction. Versions are append-only.</summary>
    public async Task<bool> SaveAsync(SaveFlowRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var definition = request.Definition;
        await using (var insert = new NpgsqlCommand("INSERT INTO flows(id) VALUES(@id) ON CONFLICT DO NOTHING",connection,tx))
        { insert.Parameters.AddWithValue("id",definition.Id); await insert.ExecuteNonQueryAsync(ct); }
        int current;
        await using (var read = new NpgsqlCommand("SELECT current_version FROM flows WHERE id=@id FOR UPDATE",connection,tx))
        { read.Parameters.AddWithValue("id",definition.Id); current = (int)(await read.ExecuteScalarAsync(ct))!; }
        var json = JsonSerializer.Serialize(definition,JsonOptions);
        // An identical retry is acknowledged even after a newer publication. It never changes the head.
        await using (var repeat = new NpgsqlCommand("SELECT definition = @document FROM flow_versions WHERE flow_id=@id AND version=@version",connection,tx))
        {
            repeat.Parameters.AddWithValue("id",definition.Id); repeat.Parameters.AddWithValue("version",definition.Version);
            repeat.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,json);
            var same = await repeat.ExecuteScalarAsync(ct);
            if (same is bool identical) { await tx.RollbackAsync(ct); return identical; }
        }
        if (current != request.ExpectedVersion || definition.Version != current + 1) { await tx.RollbackAsync(ct); return false; }
        await using var command = new NpgsqlCommand("""
            INSERT INTO flow_versions(flow_id,version,definition) VALUES(@id,@version,@document);
            INSERT INTO flow_backup_jobs(flow_id,version) VALUES(@id,@version);
            UPDATE flows SET current_version=@version,updated_at=clock_timestamp() WHERE id=@id;
            """,connection,tx);
        command.Parameters.AddWithValue("id",definition.Id); command.Parameters.AddWithValue("version",definition.Version);
        command.Parameters.AddWithValue("document",NpgsqlDbType.Jsonb,json);
        await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return true;
    }
}
