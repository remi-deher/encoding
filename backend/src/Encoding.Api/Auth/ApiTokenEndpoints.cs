using Encoding.Contracts.Auth;

namespace Encoding.Api.Auth;

public static class ApiTokenEndpoints
{
    public static void MapApiTokenEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tokens").WithTags("Tokens");

        group.MapGet("", async (ApiTokenRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct)))
            .WithName("ListApiTokens");

        group.MapPost("", async (CreateApiTokenRequest request, ApiTokenRepository repository, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Le nom du jeton est obligatoire et limité à 128 caractères."] });

            var role = string.Equals(request.Role, "viewer", StringComparison.OrdinalIgnoreCase) ? "viewer" : "admin";
            var (summary, rawToken) = await repository.CreateTokenAsync(request.Name.Trim(), role, request.ExpiresAt, ct);
            return Results.Created($"/api/v1/tokens/{summary.Id}", new CreatedApiTokenResponse(
                summary.Id, summary.Name, summary.TokenPrefix, summary.Role, summary.CreatedAt, summary.ExpiresAt, rawToken));
        }).WithName("CreateApiToken");

        group.MapDelete("/{id:guid}", async (Guid id, bool? permanent, ApiTokenRepository repository, CancellationToken ct) =>
        {
            if (permanent == true)
            {
                return await repository.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
            }

            return await repository.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName("RevokeOrDeleteApiToken");

        group.MapPost("/{id:guid}/revoke", async (Guid id, ApiTokenRepository repository, CancellationToken ct) =>
            await repository.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("RevokeApiToken");
    }
}
