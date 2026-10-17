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

/// <summary>Authentifie une requête par le cookie de session opaque, validé dans la base SQLite locale à chaque requête.</summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    UserRepository users,
    AuthOptions authOptions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Items.TryGetValue(Encoding.Api.Infrastructure.PeerCluster.PeerRequestRouting.PrincipalItem, out var forwarded)
            && forwarded is ClaimsPrincipal principal)
            return AuthenticateResult.Success(new AuthenticationTicket(principal, SessionAuthentication.Scheme));

        if (Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            var user = await users.ValidateSessionAsync(AuthService.TokenHash(token), authOptions, Context.RequestAborted);
            if (user is not null)
            {
                var identity = new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                        new Claim(ClaimTypes.Name, user.Username),
                        new Claim(ClaimTypes.Role, UserRepository.RoleName(user.Role)),
                    ],
                    SessionAuthentication.Scheme);
                return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SessionAuthentication.Scheme));
            }
            if (authOptions.Enabled) return AuthenticateResult.Fail("Session absente, expirée ou révoquée.");
        }

        if (!authOptions.Enabled)
        {
            var defaultIdentity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001"),
                    new Claim(ClaimTypes.Name, "admin"),
                    new Claim(ClaimTypes.Role, "admin"),
                ],
                SessionAuthentication.Scheme);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(defaultIdentity), SessionAuthentication.Scheme));
        }

        return AuthenticateResult.NoResult();
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
