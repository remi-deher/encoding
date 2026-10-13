using Encoding.Contracts.Libraries;
using Npgsql;

namespace Encoding.Api.Libraries;

public sealed class LibraryNodeAccessValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("La configuration des accès de bibliothèque est invalide.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class LibraryNodeAccessRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<LibraryNodeAccessResponse>?> ListAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM libraries WHERE id = @id)", connection))
        {
            exists.Parameters.AddWithValue("id", libraryId);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false) is false) return null;
        }

        const string sql = """
            SELECT a.library_id, a.node_id, n.name, a.mode, a.local_path, a.can_read, a.can_write,
                   a.transfer_source_node_id, source.name, a.updated_at
            FROM library_node_accesses a
            JOIN cluster_nodes n ON n.id = a.node_id
            LEFT JOIN cluster_nodes source ON source.id = a.transfer_source_node_id
            WHERE a.library_id = @library_id
            ORDER BY n.name, n.id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("library_id", libraryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accesses = new List<LibraryNodeAccessResponse>();
        while (await reader.ReadAsync(cancellationToken)) accesses.Add(Read(reader));
        return accesses;
    }

    public async Task<IReadOnlyList<LibraryNodeAccessResponse>?> ConfigureAsync(
        Guid libraryId,
        ConfigureLibraryNodeAccessRequest request,
        CancellationToken cancellationToken)
    {
        var nodes = request.Nodes ?? [];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM libraries WHERE id = @id)", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", libraryId);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false) is false)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        var nodeRoles = new Dictionary<Guid, string[]>();
        if (nodes.Count > 0)
        {
            await using var nodeCommand = new NpgsqlCommand("SELECT id, roles FROM cluster_nodes WHERE id = ANY(@ids)", connection, transaction);
            nodeCommand.Parameters.AddWithValue("ids", nodes.Select(item => item.NodeId).Distinct().ToArray());
            await using var nodeReader = await nodeCommand.ExecuteReaderAsync(cancellationToken);
            while (await nodeReader.ReadAsync(cancellationToken)) nodeRoles[nodeReader.GetGuid(0)] = nodeReader.GetFieldValue<string[]>(1);
        }

        var errors = Validate(nodes, nodeRoles);
        if (errors.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new LibraryNodeAccessValidationException(errors);
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM library_node_accesses WHERE library_id = @library_id", connection, transaction))
        {
            delete.Parameters.AddWithValue("library_id", libraryId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = """
            INSERT INTO library_node_accesses
                (library_id, node_id, mode, local_path, can_read, can_write, transfer_source_node_id)
            VALUES
                (@library_id, @node_id, @mode, @local_path, @can_read, @can_write, @transfer_source_node_id)
            """;
        foreach (var item in nodes)
        {
            await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
            insert.Parameters.AddWithValue("library_id", libraryId);
            insert.Parameters.AddWithValue("node_id", item.NodeId);
            insert.Parameters.AddWithValue("mode", item.Mode.ToString().ToLowerInvariant());
            insert.Parameters.AddWithValue("local_path", (object?)Normalize(item.LocalPath) ?? DBNull.Value);
            insert.Parameters.AddWithValue("can_read", item.CanRead);
            insert.Parameters.AddWithValue("can_write", item.CanWrite);
            insert.Parameters.AddWithValue("transfer_source_node_id", (object?)item.TransferSourceNodeId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ListAsync(libraryId, cancellationToken);
    }

    private static Dictionary<string, string[]> Validate(
        IReadOnlyList<LibraryNodeAccessRequest> nodes,
        IReadOnlyDictionary<Guid, string[]> registeredNodes)
    {
        var errors = new Dictionary<string, string[]>();
        if (nodes.Select(item => item.NodeId).Distinct().Count() != nodes.Count)
            errors["nodes"] = ["Chaque nœud ne peut apparaître qu’une fois."];

        var configured = nodes.GroupBy(item => item.NodeId).ToDictionary(group => group.Key, group => group.First());
        for (var index = 0; index < nodes.Count; index++)
        {
            var item = nodes[index];
            var key = $"nodes[{index}]";
            if (item.NodeId == Guid.Empty || !registeredNodes.ContainsKey(item.NodeId))
                errors[$"{key}.nodeId"] = ["Ce nœud n’existe pas dans le cluster."];

            switch (item.Mode)
            {
                case LibraryNodeAccessMode.Direct:
                    if (string.IsNullOrWhiteSpace(item.LocalPath)) errors[$"{key}.localPath"] = ["Indiquez le chemin de cette bibliothèque sur le nœud."];
                    if (!item.CanRead) errors[$"{key}.canRead"] = ["L’accès direct doit autoriser la lecture de la source."];
                    if (item.TransferSourceNodeId is not null) errors[$"{key}.transferSourceNodeId"] = ["Un accès direct ne peut pas référencer un nœud source."];
                    break;
                case LibraryNodeAccessMode.Unsupported:
                    if (!string.IsNullOrWhiteSpace(item.LocalPath) || item.CanRead || item.CanWrite || item.TransferSourceNodeId is not null)
                        errors[key] = ["Un nœud sans accès ne peut déclarer ni chemin, ni droit, ni source de transfert."];
                    break;
                case LibraryNodeAccessMode.Transfer:
                    if (item.LocalPath is not null) errors[$"{key}.localPath"] = ["Le worker traite une copie locale temporaire ; aucun chemin de bibliothèque n’est requis."];
                    if (!item.CanRead || !item.CanWrite) errors[key] = ["Le worker doit pouvoir lire la copie et renvoyer le résultat."];
                    if (!item.TransferSourceNodeId.HasValue || item.TransferSourceNodeId == item.NodeId)
                    {
                        errors[$"{key}.transferSourceNodeId"] = ["Sélectionnez un autre nœud avec accès direct à la bibliothèque."];
                    }
                    else
                    {
                        if (!registeredNodes.TryGetValue(item.NodeId, out var roles) || !roles.Contains("worker", StringComparer.OrdinalIgnoreCase))
                            errors[$"{key}.nodeId"] = ["Le transfert doit cibler un nœud ayant le rôle worker."];
                        if (!configured.TryGetValue(item.TransferSourceNodeId.Value, out var source)
                            || source.Mode != LibraryNodeAccessMode.Direct || !source.CanRead || !source.CanWrite)
                            errors[$"{key}.transferSourceNodeId"] = ["Le nœud source doit déclarer un accès direct en lecture et en écriture dans cette configuration."];
                    }
                    break;
                default:
                    errors[$"{key}.mode"] = ["Ce mode d’accès n’est pas pris en charge."];
                    break;
            }
        }
        return errors;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LibraryNodeAccessResponse Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
        Enum.Parse<LibraryNodeAccessMode>(reader.GetString(3), true),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)));
}
