using Encoding.Api.Infrastructure.Data;
using System.Data.Common;

namespace Encoding.Api.Infrastructure;

public sealed record ClusterIdentity(Guid Id, string Name);

public sealed class ClusterIdentityRepository(DbDataSource dataSource)
{
    public async Task<ClusterIdentity> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command("SELECT id, name FROM cluster_identity WHERE singleton = true", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("L’identité du cluster est absente.");
        return new ClusterIdentity(reader.GetGuid(0), reader.GetString(1));
    }
}
