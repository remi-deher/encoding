using Encoding.Contracts.Auth;

namespace Encoding.Api.Nodes;

public static class EnrollmentWindowEndpoints
{
    public static void MapEnrollmentWindowEndpoints(this WebApplication app)
    {
        var window = app.MapGroup("/api/v1/nodes/enrollment-window").WithTags("Nodes");
        window.MapGet("", async (EnrollmentWindowRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetAsync(ct))).WithName("GetEnrollmentWindow");
        window.MapPut("", async (OpenEnrollmentWindowRequest request, EnrollmentWindowRepository repository, CancellationToken ct) =>
        {
            if (request.Minutes is < 1 or > EnrollmentWindowRepository.MaximumMinutes)
                return (IResult)Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minutes"] = [$"La durée doit être comprise entre 1 et {EnrollmentWindowRepository.MaximumMinutes} minutes."],
                });
            return Results.Ok(await repository.OpenAsync(request.Minutes, ct));
        }).WithName("OpenEnrollmentWindow");
        window.MapDelete("", async (EnrollmentWindowRepository repository, CancellationToken ct) =>
        {
            await repository.CloseAsync(ct);
            return Results.NoContent();
        }).WithName("CloseEnrollmentWindow");
    }
}
