using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Auth;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Operations;
using Encoding.Contracts.Auth;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Libraries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApiAssembly = Encoding.Api.Auth.AuthOptions;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Vérifie les améliorations de l’API : OpenAPI 3.1.0, ProblemDetails RFC 7807, Endpoint Filters,
/// pagination/filtrage des listes, flux SSE temps réel et Rate Limiting sur les opérations lourdes.
/// </summary>
internal static class ApiImprovementsScenarios
{
    private const string AdminPassword = "correct horse battery staple";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync(string connection)
    {
        await using var database = await TestDatabase.CreateAsync(connection, "api_improvements");
        var directory = Path.Combine(Path.GetTempPath(), "api_imp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var secretFile = Path.Combine(directory, Encoding.Core.Nodes.LocalEnrollment.FileName);
            Encoding.Core.Nodes.LocalEnrollment.Create(secretFile);

            await using var factory = CreateFactory(database, directory, new()
            {
                ["Auth:AnonymousRequestsPerMinute"] = "1000",
                ["Auth:HeavyOperationsPerMinute"] = "3",
                [Encoding.Core.Nodes.LocalEnrollment.SecretFileSetting] = secretFile,
                [Encoding.Api.Nodes.LocalNodeEndpoints.BaseUrlSetting] = "http://example.invalid/",
            });

            var anonymous = new TestClient(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));
            var admin = new TestClient(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));

            // Initialisation de l'administrateur
            using (var setup = await anonymous.SendAsync(HttpMethod.Post, "/api/v1/auth/setup", new SetupRequest("admin", AdminPassword, NodeDisplayName: "Noeud API"), csrf: false))
            {
                Check(setup.StatusCode == HttpStatusCode.Created, "Setup succeeds.");
            }
            Check(await admin.LoginAsync("admin", AdminPassword) == HttpStatusCode.OK, "Admin login succeeds.");

            // 1. OpenAPI 3.1.0 : fermé aux anonymes, disponible pour toute session authentifiée
            Check(await anonymous.StatusAsync(HttpMethod.Get, "/api/v1/openapi.json") == HttpStatusCode.Unauthorized,
                "OpenAPI specification requires an authenticated session.");
            using (var openApiResp = await admin.SendAsync(HttpMethod.Get, "/api/v1/openapi.json"))
            {
                Check(openApiResp.StatusCode == HttpStatusCode.OK, "Authenticated user fetches /api/v1/openapi.json.");
                var doc = await openApiResp.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(doc.GetProperty("openapi").GetString() == "3.1.0", "OpenAPI version is 3.1.0.");
                Check(doc.GetProperty("info").GetProperty("title").GetString() == "Encoding Cluster API", "OpenAPI info title is populated.");
                var paths = doc.GetProperty("paths");
                Check(paths.TryGetProperty("/api/v1/executions", out _), "OpenAPI paths contain /api/v1/executions.");
                Check(paths.TryGetProperty("/api/v1/executions/stream", out _), "OpenAPI paths contain /api/v1/executions/stream.");
                Check(paths.TryGetProperty("/api/v1/flows/{id}", out _), "OpenAPI normalizes route parameters ({id:guid} -> {id}).");
                var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
                Check(schemes.TryGetProperty("sessionCookie", out _) && schemes.TryGetProperty("csrfHeader", out _),
                    "OpenAPI components document sessionCookie and csrfHeader security schemes.");
            }
            Check(await admin.StatusAsync(HttpMethod.Get, "/openapi/v1.json") == HttpStatusCode.OK,
                "Standard /openapi/v1.json alias is also exposed.");
            Console.WriteLine("PASS: OpenAPI 3.1.0 specification endpoint.");

            // 2. Standardisation RFC 7807 ProblemDetails & Endpoint Filters
            using (var invalidLib = await admin.SendAsync(HttpMethod.Post, "/api/v1/libraries", new CreateLibraryRequest("   "), correlationId: "corr-test-400"))
            {
                Check(invalidLib.StatusCode == HttpStatusCode.BadRequest, "Empty library name is rejected by ValidateLibraryNameFilter.");
                var problem = await invalidLib.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(problem.GetProperty("status").GetInt32() == 400, "ProblemDetails includes status 400.");
                Check(problem.GetProperty("correlationId").GetString() == "corr-test-400", "ProblemDetails includes correlationId extension.");
                Check(problem.GetProperty("errors").TryGetProperty("name", out _), "ValidationProblem includes field errors.");
            }

            var invalidDraftId = Guid.NewGuid();
            using (var invalidDraft = await admin.SendAsync(
                HttpMethod.Put,
                $"/api/v1/flows/{invalidDraftId}/draft",
                new SaveFlowDraftRequest(0, new FlowDefinition(1, Guid.NewGuid(), 1, "Invalid", "video", [], [])),
                correlationId: "corr-draft-400"))
            {
                Check(invalidDraft.StatusCode == HttpStatusCode.BadRequest, "Mismatched flow draft ID is rejected by ValidateFlowDraftFilter.");
                var problem = await invalidDraft.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(problem.GetProperty("title").GetString()!.Contains("invalide"), "ProblemDetails title is preserved.");
                Check(problem.GetProperty("correlationId").GetString() == "corr-draft-400", "ProblemDetails correlationId matches request.");
            }

            using (var notFoundFlow = await admin.SendAsync(HttpMethod.Get, $"/api/v1/flows/{Guid.NewGuid()}", correlationId: "corr-404"))
            {
                Check(notFoundFlow.StatusCode == HttpStatusCode.NotFound, "Unknown flow returns 404.");
                var problem = await notFoundFlow.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(problem.GetProperty("status").GetInt32() == 404 && problem.GetProperty("correlationId").GetString() == "corr-404",
                    "404 response is formatted as RFC 7807 ProblemDetails with correlationId.");
            }

            using (var deprecatedExec = await admin.SendAsync(HttpMethod.Post, "/api/v1/executions", new { }))
            {
                Check(deprecatedExec.StatusCode == HttpStatusCode.Gone, "Deprecated POST /api/v1/executions returns 410 Gone ProblemDetails.");
            }
            Console.WriteLine("PASS: RFC 7807 ProblemDetails and Endpoint Filters.");

            // 3. Pagination et filtrage sur les bibliothèques et les exécutions
            LibraryResponse createdLib;
            using (var libResp = await admin.SendAsync(HttpMethod.Post, "/api/v1/libraries", new CreateLibraryRequest("Films 4K")))
            {
                Check(libResp.StatusCode == HttpStatusCode.Created, "Library creation succeeds.");
                createdLib = (await libResp.Content.ReadFromJsonAsync<LibraryResponse>(Json))!;
            }

            var execRepo = factory.Services.GetRequiredService<ExecutionRepository>();
            var dataSource = factory.Services.GetRequiredService<System.Data.Common.DbDataSource>();
            var exec1 = Guid.NewGuid();
            var exec2 = Guid.NewGuid();
            var dummyDef = JsonSerializer.Serialize(new FlowDefinition(1, Guid.NewGuid(), 1, "Flow HEVC", "video", [], []), Json);
            await using (var conn = await dataSource.OpenConnectionAsync())
            await using (var tx = await conn.BeginTransactionAsync())
            {
                foreach (var (id, media) in new[] { (exec1, "alpha.mkv"), (exec2, "beta.mkv") })
                {
                    await using var cmd = Encoding.Api.Infrastructure.Data.Sql.Command(
                        "INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,detail) VALUES(@id,@media,'Films 4K','Flow HEVC',1,@def,'queued',1,'En attente.')",
                        conn,
                        tx);
                    cmd.Parameters.AddWithValue("id", id);
                    cmd.Parameters.AddWithValue("media", media);
                    cmd.Parameters.AddWithValue("def", Encoding.Api.Infrastructure.Data.SqlKind.Json, dummyDef);
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }
            await execRepo.CancelAsync(exec2, CancellationToken.None);

            // Filtrage par state=queued et pagination limit=1
            using (var filteredResp = await admin.SendAsync(HttpMethod.Get, "/api/v1/executions?state=queued&limit=1&offset=0"))
            {
                Check(filteredResp.StatusCode == HttpStatusCode.OK, "Filtered executions endpoint succeeds.");
                var overview = (await filteredResp.Content.ReadFromJsonAsync<ExecutionOverviewResponse>(Json))!;
                Check(overview.TotalCount == 1 && overview.Limit == 1 && overview.Offset == 0, "Filtered totalCount, limit, and offset are returned.");
                Check(overview.Executions.Count == 1 && overview.Executions[0].Id == exec1, "Only the queued execution is returned.");
            }

            // Pagination des événements d'exécution avec afterRevision
            using (var eventsResp = await admin.SendAsync(HttpMethod.Get, $"/api/v1/executions/{exec2}/events?afterRevision=0&limit=10"))
            {
                Check(eventsResp.StatusCode == HttpStatusCode.OK, "Filtered execution events endpoint succeeds.");
                var events = (await eventsResp.Content.ReadFromJsonAsync<List<ExecutionEventResponse>>(Json))!;
                Check(events.Count == 1 && events[0].Revision == 1 && events[0].State == "cancelled",
                    "afterRevision=0 returns only the cancellation event at revision 1.");
            }
            Console.WriteLine("PASS: executions and events pagination and filtering.");

            // 4. Flux temps réel SSE (Server-Sent Events)
            using (var sseOnce = await admin.SendAsync(HttpMethod.Get, "/api/v1/executions/stream?once=true"))
            {
                Check(sseOnce.StatusCode == HttpStatusCode.OK, "SSE overview stream returns 200 OK.");
                Check(sseOnce.Content.Headers.ContentType?.MediaType == "text/event-stream", "SSE response has text/event-stream media type.");
                var payload = await sseOnce.Content.ReadAsStringAsync();
                Check(payload.StartsWith("event: overview\ndata: ") && payload.Contains("alpha.mkv"), "SSE stream emits a well-formed overview event.");
            }

            using (var sseDetail = await admin.SendAsync(HttpMethod.Get, $"/api/v1/executions/{exec1}/stream?once=true"))
            {
                Check(sseDetail.StatusCode == HttpStatusCode.OK, "SSE execution detail stream returns 200 OK.");
                var payload = await sseDetail.Content.ReadAsStringAsync();
                Check(payload.StartsWith("event: detail\ndata: ") && payload.Contains(exec1.ToString()), "SSE detail stream emits execution detail snapshot.");
            }
            Console.WriteLine("PASS: Server-Sent Events (SSE) real-time endpoints.");

            // 5. Rate Limiting sur les opérations lourdes (HeavyOperationsPolicy = 3/min)
            var discoverStatuses = new List<HttpStatusCode>();
            for (var i = 0; i < 4; i++)
            {
                using var resp = await admin.SendAsync(HttpMethod.Post, $"/api/v1/libraries/{createdLib.Id}/discover", new { });
                discoverStatuses.Add(resp.StatusCode);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
                    Check(problem.GetProperty("status").GetInt32() == 429, "Rate limit 429 response is formatted as ProblemDetails.");
                }
            }
            Check(discoverStatuses.Take(3).All(s => s == HttpStatusCode.Conflict) && discoverStatuses[3] == HttpStatusCode.TooManyRequests,
                "Heavy operations are rate-limited after the configured permit limit: " + string.Join(",", discoverStatuses));
            Console.WriteLine("PASS: HeavyOperationsPolicy rate limiting.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static WebApplicationFactory<ApiAssembly> CreateFactory(TestDatabase cluster, string directory, Dictionary<string, string> settings) =>
        new WebApplicationFactory<ApiAssembly>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
            foreach (var (key, value) in cluster.Settings()) builder.UseSetting(key, value);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            builder.UseSetting("Auth:Enabled", "true");
            builder.UseSetting("FlowBackups:Directory", Path.Combine(directory, "backups"));
            builder.UseSetting("Runtime:ArtifactDirectory", Path.Combine(directory, "artifacts"));
            builder.UseSetting("PluginSettings:KeyDirectory", Path.Combine(directory, "keys"));
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(descriptor);
            });
        });

    private sealed class TestClient(HttpClient http)
    {
        public string? Cookie { get; set; }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool csrf = true, string? correlationId = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            if (Cookie is not null) request.Headers.Add("Cookie", $"{SessionAuthentication.CookieName}={Cookie}");
            if (csrf && method != HttpMethod.Get) request.Headers.Add(ManagementAccessHandler.CsrfHeader, "1");
            if (correlationId is not null) request.Headers.Add(OperationsEndpoints.CorrelationHeader, correlationId);
            return await http.SendAsync(request);
        }

        public async Task<HttpStatusCode> StatusAsync(HttpMethod method, string url, object? body = null, bool csrf = true)
        {
            using var response = await SendAsync(method, url, body, csrf);
            return response.StatusCode;
        }

        public async Task<HttpStatusCode> LoginAsync(string username, string password)
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(username, password), csrf: false);
            if (response.IsSuccessStatusCode)
            {
                var header = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(SessionAuthentication.CookieName + "="));
                Cookie = header[(SessionAuthentication.CookieName.Length + 1)..header.IndexOf(';')];
            }
            return response.StatusCode;
        }
    }
}
