using System.Data.Common;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.PeerCluster;
using Encoding.Contracts.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Encoding.Api.Logging;

public sealed class SystemLogRepository
{
    private readonly DbDataSource _dataSource;

    [ActivatorUtilitiesConstructor]
    public SystemLogRepository(PeerLocalDatabase localDb)
    {
        ArgumentNullException.ThrowIfNull(localDb);
        _dataSource = localDb.Source;
    }

    public SystemLogRepository(DbDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task InsertAsync(SystemLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("""
            INSERT INTO system_logs (id, timestamp, level, category, message, exception, node_id, node_name, event_id)
            VALUES (@id, @timestamp, @level, @category, @message, @exception, @node_id, @node_name, @event_id)
            """, connection);

        cmd.Parameters.AddWithValue("id", entry.Id);
        cmd.Parameters.AddWithValue("timestamp", entry.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("level", entry.Level.ToString());
        cmd.Parameters.AddWithValue("category", entry.Category);
        cmd.Parameters.AddWithValue("message", entry.Message);
        cmd.Parameters.AddWithValue("exception", (object?)entry.Exception ?? DBNull.Value);
        cmd.Parameters.AddWithValue("node_id", (object?)entry.NodeId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("node_name", (object?)entry.NodeName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("event_id", (object?)entry.EventId ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SystemLogEntry>> QueryAsync(SystemLogFilterQuery query, CancellationToken ct = default)
    {
        query ??= new SystemLogFilterQuery();
        var limit = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, 1000);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        var sql = "SELECT id, timestamp, level, category, message, exception, node_id, node_name, event_id FROM system_logs WHERE 1=1";

        if (query.MinLevel.HasValue)
        {
            var allowedLevels = query.MinLevel.Value switch
            {
                SystemLogLevel.Critical => "('Critical')",
                SystemLogLevel.Error => "('Error', 'Critical')",
                _ => "('Warning', 'Error', 'Critical')"
            };
            sql += $" AND level IN {allowedLevels}";
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            sql += " AND category LIKE @category";
        }

        if (query.NodeId.HasValue)
        {
            sql += " AND node_id = @node_id";
        }

        if (query.Since.HasValue)
        {
            sql += " AND timestamp > @since";
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            sql += " AND (message LIKE @search OR (exception IS NOT NULL AND exception LIKE @search))";
        }

        sql += " ORDER BY timestamp DESC LIMIT @limit";

        await using var cmd = Sql.Command(sql, connection);
        cmd.Parameters.AddWithValue("limit", limit);

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            cmd.Parameters.AddWithValue("category", query.Category.Trim() + "%");
        }

        if (query.NodeId.HasValue)
        {
            cmd.Parameters.AddWithValue("node_id", query.NodeId.Value.ToString());
        }

        if (query.Since.HasValue)
        {
            cmd.Parameters.AddWithValue("since", query.Since.Value.ToString("o"));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            cmd.Parameters.AddWithValue("search", "%" + query.Search.Trim() + "%");
        }

        var results = new List<SystemLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var timestamp = DateTimeOffset.Parse(reader.GetString(1));
            var levelStr = reader.GetString(2);
            var level = Enum.TryParse<SystemLogLevel>(levelStr, true, out var parsedLevel)
                ? parsedLevel
                : SystemLogLevel.Warning;
            var category = reader.GetString(3);
            var message = reader.GetString(4);
            var exception = reader.IsDBNull(5) ? null : reader.GetString(5);
            var nodeId = reader.IsDBNull(6) ? null : (Guid.TryParse(reader.GetString(6), out var nid) ? nid : (Guid?)null);
            var nodeName = reader.IsDBNull(7) ? null : reader.GetString(7);
            var eventId = reader.IsDBNull(8) ? null : (int?)reader.GetInt32(8);

            results.Add(new SystemLogEntry(
                Id: id,
                Timestamp: timestamp,
                Level: level,
                Category: category,
                Message: message,
                Exception: exception,
                NodeId: nodeId,
                NodeName: nodeName,
                EventId: eventId));
        }

        return results;
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("SELECT DISTINCT category FROM system_logs ORDER BY category", connection);

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    public async Task PruneAsync(int maxRows = 5000, TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        maxAge ??= TimeSpan.FromDays(7);
        var cutoff = (DateTimeOffset.UtcNow - maxAge.Value).ToString("o");

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // 1. Purge des entrées plus vieilles que maxAge (7 jours)
        await using (var deleteOlder = Sql.Command("DELETE FROM system_logs WHERE timestamp < @cutoff", connection))
        {
            deleteOlder.Parameters.AddWithValue("cutoff", cutoff);
            await deleteOlder.ExecuteNonQueryAsync(ct);
        }

        // 2. Rétention bornée à maxRows (5 000 lignes)
        await using (var pruneExcess = Sql.Command("""
            DELETE FROM system_logs WHERE id IN (
                SELECT id FROM system_logs ORDER BY timestamp DESC LIMIT -1 OFFSET @maxRows
            )
            """, connection))
        {
            pruneExcess.Parameters.AddWithValue("maxRows", maxRows);
            await pruneExcess.ExecuteNonQueryAsync(ct);
        }
    }
}
