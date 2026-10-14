using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Encoding.Api.Auth;

public static class SessionAuthentication
{
    public const string Scheme = "ClusterSession";
    public const string CookieName = "encoding_session";
}

/// <summary>Authentifie une requête par le cookie de session opaque, validé dans PostgreSQL à chaque requête.</summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    UserRepository users,
    AuthOptions authOptions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var token) || string.IsNullOrWhiteSpace(token))
            return AuthenticateResult.NoResult();
        var user = await users.ValidateSessionAsync(AuthService.TokenHash(token), authOptions, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("Session absente, expirée ou révoquée.");
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, UserRepository.RoleName(user.Role)),
            ],
            SessionAuthentication.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SessionAuthentication.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
