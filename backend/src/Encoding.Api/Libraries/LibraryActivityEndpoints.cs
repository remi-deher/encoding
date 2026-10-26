using Encoding.Contracts.Libraries;
using Encoding.Api.Infrastructure;

namespace Encoding.Api.Libraries;

public static class LibraryActivityEndpoints
{
    public static void MapLibraryActivityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/activity/files", async (
            string? status,
            Guid? libraryId,
            string? flow,
            Guid? nodeId,
            string? search,
            int? limit,
            int? offset,
            LibraryActivityRepository repository,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(status) &&
                status.Trim().ToLowerInvariant() is not ("untreated" or "active" or "done" or "failed" or "disabled"))
                return ApiProblem.BadRequest("État d’activité invalide.");
            return Results.Ok(await repository.GetAsync(status, libraryId, flow, nodeId, search, limit, offset, ct));
        })
            .WithName("GetLibraryActivityFiles");
    }
}
