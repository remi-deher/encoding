using Npgsql;

namespace Encoding.Api.Infrastructure;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }))
            .WithTags("Health");
        app.MapGet("/health/ready", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return (IResult)Results.Ok(new { status = "ready" });
            }
            catch (NpgsqlException)
            {
                return (IResult)Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Base de données indisponible.");
            }
        }).WithTags("Health");
    }
}
