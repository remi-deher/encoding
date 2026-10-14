namespace Encoding.Api.Auth;

/// <summary>Paramètres d’authentification, identiques pour tous les serveurs du cluster.</summary>
public sealed record AuthOptions(
    TimeSpan SessionIdleTimeout,
    TimeSpan SessionAbsoluteLifetime,
    bool? SecureCookie,
    string? SetupToken,
    int MaxFailedAttempts,
    int MaxPendingPairingRequests,
    int AnonymousRequestsPerMinute)
{
    public static AuthOptions From(IConfiguration configuration)
    {
        var idle = configuration.GetValue("Auth:SessionIdleMinutes", 480);
        var absolute = configuration.GetValue("Auth:SessionAbsoluteHours", 168);
        var maxFailures = configuration.GetValue("Auth:MaxFailedAttempts", 5);
        var maxPending = configuration.GetValue("Auth:MaxPendingPairingRequests", 20);
        var perMinute = configuration.GetValue("Auth:AnonymousRequestsPerMinute", 20);
        if (perMinute <= 0) throw new InvalidOperationException("Auth:AnonymousRequestsPerMinute doit être supérieur à zéro.");
        if (idle <= 0) throw new InvalidOperationException("Auth:SessionIdleMinutes doit être supérieur à zéro.");
        if (absolute <= 0) throw new InvalidOperationException("Auth:SessionAbsoluteHours doit être supérieur à zéro.");
        if (maxFailures <= 0) throw new InvalidOperationException("Auth:MaxFailedAttempts doit être supérieur à zéro.");
        if (maxPending <= 0) throw new InvalidOperationException("Auth:MaxPendingPairingRequests doit être supérieur à zéro.");
        var secure = configuration["Auth:CookieSecure"];
        var setupToken = configuration["Auth:SetupToken"];
        return new AuthOptions(
            TimeSpan.FromMinutes(idle),
            TimeSpan.FromHours(absolute),
            string.IsNullOrWhiteSpace(secure) || secure.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : bool.Parse(secure),
            string.IsNullOrWhiteSpace(setupToken) ? null : setupToken,
            maxFailures,
            maxPending,
            perMinute);
    }
}
