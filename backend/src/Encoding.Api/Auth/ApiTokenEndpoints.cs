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

        group.MapGet("/replication", async (Encoding.Api.Tools.AiClusterService cluster, CancellationToken ct) =>
            Results.Ok(await cluster.ApiTokenReplicationAsync(ct)))
            .WithName("ApiTokenReplication").RequireAuthorization(AuthExtensions.AdministratorPolicy);

        group.MapPost("", async (CreateApiTokenRequest request, HttpContext http, AuthService auth, ApiTokenRepository repository, CancellationToken ct) =>
        {
            var current = AuthEndpoints.Current(http.User);
            if (current is null || http.User.FindFirst("auth_type")?.Value == "api_token"
                || string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > AuthService.MaximumPasswordLength
                || !await auth.ReauthenticateAsync(current.Id, request.CurrentPassword, ct))
                return Results.Json(new { title = "Saisissez le mot de passe de votre compte pour afficher le nouveau jeton." }, statusCode: StatusCodes.Status401Unauthorized);
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Le nom du jeton est obligatoire et limité à 128 caractères."] });

            var requestedRole = request.Role?.Trim();
            if (!string.IsNullOrEmpty(requestedRole)
                && !string.Equals(requestedRole, "admin", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requestedRole, "viewer", StringComparison.OrdinalIgnoreCase))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Le rôle d’un jeton est « admin » ou « viewer »."] });
            var role = string.Equals(requestedRole, "viewer", StringComparison.OrdinalIgnoreCase) ? "viewer" : "admin";

            var (scopes, unknown) = ApiTokenScopes.Normalize(request.Scopes);
            if (unknown.Count > 0)
                return Results.ValidationProblem(new Dictionary<string,
                    string[]> { ["scopes"] = [$"Portées inconnues : {string.Join(", ", unknown)}. Valeurs possibles : {string.Join(", ", ApiTokenScopes.Allowed)}."] });
            if (role == "viewer" && scopes.Count > 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["scopes"] = ["Un jeton en lecture seule ne peut pas porter de portée d’écriture."] });

            var (summary, rawToken) = await repository.CreateTokenAsync(request.Name.Trim(), role, request.ExpiresAt, ct, scopes);
            return Results.Created($"/api/v1/tokens/{summary.Id}", new CreatedApiTokenResponse(
                summary.Id, summary.Name, summary.TokenPrefix, summary.Role, summary.CreatedAt, summary.ExpiresAt, rawToken, summary.Scopes));
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
