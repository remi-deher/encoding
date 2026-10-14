using System.Threading.RateLimiting;
using Encoding.Api.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace Encoding.Api.Auth;

public static class AuthExtensions
{
    public const string AnonymousPolicy = "anonymous-protocol";
    public const string AdministratorPolicy = "Administrator";

    public static IServiceCollection AddClusterAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = AuthOptions.From(configuration);
        services.AddSingleton(options);
        services.AddSingleton<UserRepository>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<IAuthorizationHandler, ManagementAccessHandler>();
        services.AddAuthentication(SessionAuthentication.Scheme)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthentication.Scheme, null);
        services.AddAuthorization(authorization =>
        {
            var requirement = new ManagementAccessRequirement();
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder(SessionAuthentication.Scheme).AddRequirements(requirement).Build();
            authorization.AddPolicy(AdministratorPolicy, policy => policy
                .AddAuthenticationSchemes(SessionAuthentication.Scheme).RequireRole("admin").AddRequirements(requirement));
        });
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Quota par adresse source pour les routes joignables sans compte (connexion, initialisation, jumelage des nœuds).
            limiter.AddPolicy(AnonymousPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.AnonymousRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        if (configuration.GetValue("Auth:TrustForwardedHeaders", false))
        {
            services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.KnownIPNetworks.Clear();
                forwarded.KnownProxies.Clear();
            });
        }
        return services;
    }

    public static WebApplication UseClusterAuthentication(this WebApplication app)
    {
        if (app.Configuration.GetValue("Auth:TrustForwardedHeaders", false)) app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        return app;
    }
}
