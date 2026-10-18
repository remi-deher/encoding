using Encoding.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Exposition du schéma OpenAPI 3.1.0 généré à partir du graphe de routes Minimal API et des métadonnées d’autorisation.
/// </summary>
public static class OpenApiEndpoints
{
    public static void MapOpenApiEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/openapi.json", (EndpointDataSource dataSource) =>
            Results.Json(BuildSpecification(dataSource)))
            .WithTags("OpenAPI")
            .WithName("GetOpenApiSpecification");

        app.MapGet("/openapi/v1.json", (EndpointDataSource dataSource) =>
            Results.Json(BuildSpecification(dataSource)))
            .WithTags("OpenAPI")
            .WithName("GetStandardOpenApiSpecification");
    }

    public static object BuildSpecification(EndpointDataSource dataSource)
    {
        var paths = new SortedDictionary<string, SortedDictionary<string, object>>(StringComparer.Ordinal);
        var tags = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var rawPattern = endpoint.RoutePattern.RawText;
            if (string.IsNullOrWhiteSpace(rawPattern) || rawPattern.Contains("{*"))
                continue;

            var normalizedPath = NormalizeRoutePattern(rawPattern, out var pathParameters);
            if (!paths.TryGetValue(normalizedPath, out var operations))
            {
                operations = new SortedDictionary<string, object>(StringComparer.Ordinal);
                paths[normalizedPath] = operations;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            var endpointTags = endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? [];
            foreach (var tag in endpointTags) tags.Add(tag);

            var operationId = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
            var isAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var allowsNode = endpoint.Metadata.GetMetadata<NodeCredentialAccessMetadata>() is not null;
            var allowsViewer = endpoint.Metadata.GetMetadata<ViewerAccessMetadata>() is not null;
            var accepts = endpoint.Metadata.GetOrderedMetadata<IAcceptsMetadata>();

            foreach (var method in methods)
            {
                var lowerMethod = method.ToLowerInvariant();
                var safeMethod = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
                var security = BuildSecurityRequirements(isAnonymous, allowsNode, safeMethod);
                var roleSummary = isAnonymous
                    ? "Public / Protocole"
                    : safeMethod || allowsViewer
                        ? "Session authentifiée (viewer ou admin)"
                        : "Session administrateur + protection CSRF";

                var operation = new Dictionary<string, object?>
                {
                    ["summary"] = $"{method} {normalizedPath} ({roleSummary})",
                    ["tags"] = endpointTags.Count > 0 ? endpointTags : ["General"],
                    ["parameters"] = pathParameters,
                    ["security"] = security,
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new { description = "Opération réussie." },
                        ["400"] = new { description = "Requête invalide (RFC 7807 ProblemDetails)." },
                        ["401"] = new { description = "Authentification requise." },
                        ["403"] = new { description = "Accès refusé (rôle insuffisant ou en-tête CSRF manquant)." },
                        ["429"] = new { description = "Limite de débit atteinte (Rate Limiting)." },
                    },
                };

                if (!string.IsNullOrWhiteSpace(operationId))
                {
                    operation["operationId"] = methods.Count == 1 ? operationId : $"{operationId}_{lowerMethod}";
                }

                if (!safeMethod && accepts.Count > 0)
                {
                    var contentTypes = accepts.SelectMany(item => item.ContentTypes).Distinct().ToArray();
                    if (contentTypes.Length == 0) contentTypes = ["application/json"];
                    operation["requestBody"] = new
                    {
                        required = true,
                        content = contentTypes.ToDictionary(
                            contentType => contentType,
                            _ => (object)new { schema = new { type = "object" } }),
                    };
                }

                operations[lowerMethod] = operation;
            }
        }

        return new Dictionary<string, object>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = "Encoding Cluster API",
                ["version"] = "1.0.0",
                ["description"] = "API d’orchestration distribuée d’encodage multimédia, gestion des flows, bibliothèques et nœuds du cluster.",
            },
            ["tags"] = tags.Select(name => new { name }).ToArray(),
            ["paths"] = paths,
            ["components"] = new Dictionary<string, object>
            {
                ["securitySchemes"] = new Dictionary<string, object>
                {
                    ["sessionCookie"] = new
                    {
                        type = "apiKey",
                        @in = "cookie",
                        name = SessionAuthentication.CookieName,
                        description = "Cookie de session HttpOnly SameSite=Strict.",
                    },
                    ["csrfHeader"] = new
                    {
                        type = "apiKey",
                        @in = "header",
                        name = ManagementAccessHandler.CsrfHeader,
                        description = "En-tête anti-CSRF obligatoire (valeur '1') pour les mutations authentifiées par session.",
                    },
                    ["nodeToken"] = new
                    {
                        type = "apiKey",
                        @in = "header",
                        name = "X-Node-Token",
                        description = "Jeton d’authentification des workers enrôlés.",
                    },
                    ["bearerAuth"] = new
                    {
                        type = "http",
                        scheme = "bearer",
                        bearerFormat = "Token API Cluster",
                        description = "Jeton d’authentification API cluster (ex. enc_pat_...). Valable sur n’importe quel nœud du cluster, dispense de cookie et d'en-tête anti-CSRF.",
                    },
                },
                ["schemas"] = new Dictionary<string, object>
                {
                    ["ProblemDetails"] = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["type"] = new { type = "string" },
                            ["title"] = new { type = "string" },
                            ["status"] = new { type = "integer" },
                            ["detail"] = new { type = "string" },
                            ["instance"] = new { type = "string" },
                            ["correlationId"] = new { type = "string" },
                        },
                    },
                },
            },
        };
    }

    private static object[] BuildSecurityRequirements(bool isAnonymous, bool allowsNode, bool safeMethod)
    {
        if (isAnonymous) return [];
        var requirements = new List<object>
        {
            new Dictionary<string, string[]> { ["bearerAuth"] = [] }
        };
        if (safeMethod)
        {
            requirements.Add(new Dictionary<string, string[]> { ["sessionCookie"] = [] });
            if (allowsNode)
                requirements.Add(new Dictionary<string, string[]> { ["nodeToken"] = [] });
        }
        else
        {
            requirements.Add(new Dictionary<string, string[]>
            {
                ["sessionCookie"] = [],
                ["csrfHeader"] = [],
            });
        }
        return requirements.ToArray();
    }

    private static string NormalizeRoutePattern(string rawPattern, out IReadOnlyList<object> parameters)
    {
        var list = new List<object>();
        var trimmed = rawPattern.Length > 1 ? rawPattern.TrimEnd('/') : rawPattern;
        var segments = trimmed.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                var inner = segment[1..^1];
                var parts = inner.Split(':', 2);
                var paramName = parts[0];
                var constraint = parts.Length > 1 ? parts[1] : null;
                segments[i] = $"{{{paramName}}}";
                list.Add(new
                {
                    name = paramName,
                    @in = "path",
                    required = true,
                    schema = constraint switch
                    {
                        "guid" => (object)new { type = "string", format = "uuid" },
                        "int" => new { type = "integer", format = "int32" },
                        _ => new { type = "string" },
                    },
                });
            }
        }
        parameters = list;
        return string.Join('/', segments);
    }
}
