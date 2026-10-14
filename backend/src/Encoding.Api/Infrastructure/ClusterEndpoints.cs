

namespace Encoding.Api.Infrastructure;

public static class ClusterEndpoints
{
    public static void MapClusterEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/cluster", async (ClusterIdentityRepository repository, CancellationToken cancellationToken) =>
        {
            var identity = await repository.GetAsync(cancellationToken);
            return Results.Ok(new { identity.Id, identity.Name });
        }).WithTags("Cluster").WithName("GetClusterIdentity");
    }
}
