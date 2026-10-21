using System.Threading.RateLimiting;
using Encoding.Api.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Encoding.Api.Auth;

public static class AuthExtensions
{
    public const string AnonymousPolicy = "anonymous-protocol";
    public const string ClusterMeshPolicy = "cluster-mesh";
    public const string HeavyOperationsPolicy = "heavy-operations";
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
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await Encoding.Api.Infrastructure.ApiProblem.TooManyRequests()
                    .ExecuteAsync(context.HttpContext);
            };
            // Quota par adresse source pour les routes joignables sans compte (connexion, initialisation, jumelage des nœuds).
            limiter.AddPolicy(AnonymousPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.AnonymousRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(ClusterMeshPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Quota pour les opérations intensives (découverte disque, import/export ZIP de flows).
            limiter.AddPolicy(HeavyOperationsPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.HeavyOperationsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        if (configuration.GetValue("Auth:TrustForwardedHeaders", false))
        {
            services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.KnownIPNetworks.Clear();
                forwarded.KnownProxies.Clear();
                // Sans liste, tout appelant direct est cru sur parole (comportement historique, à éviter hors réseau fermé).
                foreach (var entry in SplitList(configuration["Auth:KnownProxies"]))
                    forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(entry));
                foreach (var entry in SplitList(configuration["Auth:KnownNetworks"]))
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
            });
        }
        return services;
    }

    private static IEnumerable<string> SplitList(string? value) =>
        (value ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Applique X-Forwarded-For/Proto avant les fichiers statiques et les en-têtes de sécurité (HSTS dépend du schéma réel).</summary>
    public static WebApplication UseTrustedForwardedHeaders(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Auth:TrustForwardedHeaders", false)) return app;
        if (string.IsNullOrWhiteSpace(app.Configuration["Auth:KnownProxies"]) && string.IsNullOrWhiteSpace(app.Configuration["Auth:KnownNetworks"]))
            app.Logger.LogWarning("Auth:TrustForwardedHeaders=true sans Auth:KnownProxies ni Auth:KnownNetworks : n'importe quel appelant direct peut forger X-Forwarded-For et X-Forwarded-Proto. Renseignez l'adresse du reverse proxy.");
        app.UseForwardedHeaders();
        return app;
    }

    public static WebApplication UseClusterAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        return app;
    }
}
