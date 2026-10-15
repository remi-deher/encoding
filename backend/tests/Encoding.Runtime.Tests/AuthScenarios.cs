using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Auth;
using Encoding.Contracts.Auth;
using Encoding.Contracts.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
// Encoding.Api et Encoding.NodeAgent exposent chacun un Program : un type de l’assemblage API désigne le point d’entrée à héberger.
using ApiAssembly = Encoding.Api.Auth.AuthOptions;

namespace Encoding.Runtime.Tests;

/// <summary>Authentification, rôles et durcissement du jumelage, vérifiés sur le vrai pipeline HTTP de l’API.</summary>
internal static class AuthScenarios
{
    private const string SetupToken = "jeton-d-initialisation-de-test";
    private const string AdminPassword = "correct horse battery staple";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void RunUnit()
    {
        var hash = PasswordHasher.Hash("un mot de passe solide");
        Check(hash.StartsWith("pbkdf2-sha512$") && !hash.Contains("solide"), "A password hash never contains the password.");
        Check(PasswordHasher.Hash("un mot de passe solide") != hash, "Each hash uses a fresh salt.");
        Check(PasswordHasher.Verify("un mot de passe solide", hash, out var rehash) && !rehash, "The right password verifies at the current cost.");
        Check(!PasswordHasher.Verify("un autre mot de passe", hash, out _), "A wrong password is rejected.");
        Check(!PasswordHasher.Verify("x", "not-a-hash", out _) && !PasswordHasher.Verify("x", "pbkdf2-sha512$1$@@@$@@@", out _), "Malformed stored hashes never verify or throw.");
        var weak = PasswordHasher.Verify("x", "pbkdf2-sha512$1000$" + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("x", new byte[16], 1000, System.Security.Cryptography.HashAlgorithmName.SHA512, 32)), out var upgrade);
        Check(weak && upgrade, "A hash below the current cost verifies and asks for a rehash.");
        Check(AuthService.ValidateNewUser("alice", "douze caracteres").Count == 0, "A valid account passes the policy.");
        Check(AuthService.ValidateNewUser("a b", "court").Count == 2, "Spaces in names and short passwords are both reported.");
        Check(AuthService.ValidatePassword("p", new string('x', 257)).Count == 1, "Oversized passwords are refused before hashing.");
        Console.WriteLine("PASS: password hashing, rehash detection and credential policy.");
    }

    public static async Task RunAsync(string connection)
    {
        var schema = "auth_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), schema);
        Directory.CreateDirectory(directory);
        await using (var admin = NpgsqlDataSource.Create(connection)) await admin.CreateCommand($"CREATE SCHEMA {schema}").ExecuteNonQueryAsync();
        try
        {
            var cluster = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString;
            await using (var factory = CreateFactory(cluster, directory, new() { ["Auth:AnonymousRequestsPerMinute"] = "1000", ["Auth:MaxPendingPairingRequests"] = "2" }))
            {
                await ScenarioAsync(factory);
                await EndpointCatalogueAsync(factory);
            }
            await using (var limited = CreateFactory(cluster, directory, new() { ["Auth:AnonymousRequestsPerMinute"] = "3" }))
            {
                using var client = limited.CreateClient();
                var statuses = new List<HttpStatusCode>();
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody", "mauvais mot de passe"));
                    statuses.Add(response.StatusCode);
                }
                Check(statuses.Take(3).All(status => status == HttpStatusCode.Unauthorized) && statuses.Skip(3).All(status => status == HttpStatusCode.TooManyRequests),
                    "Anonymous protocol routes are rate limited per source address: " + string.Join(",", statuses));
                Console.WriteLine("PASS: anonymous login and pairing routes are rate limited.");
            }
        }
        finally
        {
            await using var admin = NpgsqlDataSource.Create(connection);
            await admin.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE").ExecuteNonQueryAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static WebApplicationFactory<ApiAssembly> CreateFactory(string cluster, string directory, Dictionary<string, string> settings) =>
        new WebApplicationFactory<ApiAssembly>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:Cluster", cluster);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            builder.UseSetting("Auth:SetupToken", SetupToken);
            builder.UseSetting("FlowBackups:Directory", Path.Combine(directory, "backups"));
            builder.UseSetting("Runtime:ArtifactDirectory", Path.Combine(directory, "artifacts"));
            builder.UseSetting("PluginSettings:KeyDirectory", Path.Combine(directory, "keys"));
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            // Les tâches de fond (mDNS, surveillance, sauvegardes) ne participent pas à ces scénarios.
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
            });
        });

    private sealed class Client(HttpClient http)
    {
        public string? Cookie { get; set; }
        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool csrf = true, string? nodeToken = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            if (Cookie is not null) request.Headers.Add("Cookie", $"{SessionAuthentication.CookieName}={Cookie}");
            if (csrf && method != HttpMethod.Get) request.Headers.Add(ManagementAccessHandler.CsrfHeader, "1");
            if (nodeToken is not null) request.Headers.Add("X-Node-Token", nodeToken);
            return await http.SendAsync(request);
        }
        public async Task<HttpStatusCode> StatusAsync(HttpMethod method, string url, object? body = null, bool csrf = true, string? nodeToken = null)
        {
            using var response = await SendAsync(method, url, body, csrf, nodeToken);
            return response.StatusCode;
        }
        public async Task<HttpStatusCode> LoginAsync(string username, string password)
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(username, password), csrf: false);
            if (response.IsSuccessStatusCode) Cookie = ReadCookie(response);
            return response.StatusCode;
        }
    }

    private static string ReadCookie(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(SessionAuthentication.CookieName + "="));
        var lower = header.ToLowerInvariant();
        Check(lower.Contains("httponly") && lower.Contains("samesite=strict") && lower.Contains("path=/"), "The session cookie is HttpOnly, SameSite=Strict and scoped to the site: " + header);
        return header[(SessionAuthentication.CookieName.Length + 1)..header.IndexOf(';')];
    }

    private static NodeEnrollmentRequest NewNode(Guid id, string token) =>
        new(id, "worker-" + id.ToString("N")[..6], [NodeRole.Worker], 1, [new("ffprobe", "7.0", ["video"], [])], token);

    private static string NodeToken() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    private static async Task ScenarioAsync(WebApplicationFactory<ApiAssembly> factory)
    {
        var anonymous = new Client(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));
        var admin = new Client(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));
        var viewer = new Client(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));
        var get = HttpMethod.Get; var post = HttpMethod.Post; var put = HttpMethod.Put; var delete = HttpMethod.Delete;

        // Avant initialisation et sans session : tout est fermé sauf le protocole public.
        using (var status = await anonymous.SendAsync(get, "/api/v1/auth/status"))
        {
            var body = (await status.Content.ReadFromJsonAsync<AuthStatusResponse>(Json))!;
            Check(body.SetupRequired && body.User is null, "A fresh cluster reports that setup is required.");
        }
        Check(await anonymous.StatusAsync(get, "/health/live") == HttpStatusCode.OK, "Liveness stays public.");
        Check(await anonymous.StatusAsync(get, "/api/v1/cluster") == HttpStatusCode.OK, "Cluster identity stays public for worker discovery.");
        foreach (var url in new[] { "/api/v1/libraries", "/api/v1/flows", "/api/v1/nodes", "/api/v1/executions", "/api/v1/plugins/base/configuration", "/api/v1/users", "/api/v1/nodes/enrollment-requests", "/api/v1/node/mode" })
            Check(await anonymous.StatusAsync(get, url) == HttpStatusCode.Unauthorized, "Anonymous reads of " + url + " are refused.");
        Check(await anonymous.StatusAsync(post, "/api/v1/libraries", new { name = "x" }) == HttpStatusCode.Unauthorized, "Anonymous writes are refused.");

        // Initialisation du premier administrateur.
        Check(await anonymous.StatusAsync(post, "/api/v1/auth/setup", new SetupRequest("root", AdminPassword, "mauvais jeton"), csrf: false) == HttpStatusCode.Forbidden, "Setup refuses a wrong setup token.");
        Check(await anonymous.StatusAsync(post, "/api/v1/auth/setup", new SetupRequest("root", "court", SetupToken), csrf: false) == HttpStatusCode.BadRequest, "Setup enforces the password policy.");
        using (var setup = await anonymous.SendAsync(post, "/api/v1/auth/setup", new SetupRequest("root", AdminPassword, SetupToken), csrf: false))
        {
            Check(setup.StatusCode == HttpStatusCode.Created, "Setup creates the first administrator.");
            admin.Cookie = ReadCookie(setup);
            Check((await setup.Content.ReadFromJsonAsync<CurrentUserResponse>(Json))!.Role == UserRole.Admin, "The first account is an administrator.");
        }
        Check(await anonymous.StatusAsync(post, "/api/v1/auth/setup", new SetupRequest("autre", AdminPassword, SetupToken), csrf: false) == HttpStatusCode.Conflict, "Setup cannot run twice.");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 1).Select(_ => anonymous.StatusAsync(post, "/api/v1/auth/setup", new SetupRequest("racer", AdminPassword, SetupToken), csrf: false)));
        Check(concurrent.All(code => code == HttpStatusCode.Conflict), "A later setup attempt never creates a second administrator.");

        // Administrateur : lecture, écriture avec protection CSRF.
        Check(await admin.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.OK, "An administrator reads.");
        Check(await admin.StatusAsync(put, "/api/v1/node/mode", new { server = true, worker = true }) == HttpStatusCode.Conflict, "Without a launcher, an administrator gets a clear refusal to change the node mode.");
        Check(await admin.StatusAsync(post, "/api/v1/libraries", new { name = "sans-csrf" }, csrf: false) == HttpStatusCode.Forbidden, "Cookie-authenticated writes need the anti-CSRF header.");
        Check(await admin.StatusAsync(post, "/api/v1/libraries", new { name = "films" }) == HttpStatusCode.Created, "An administrator writes.");
        Check(await admin.StatusAsync(get, "/api/v1/users") == HttpStatusCode.OK, "Administrators list users.");

        // Rôle lecture.
        Check(await admin.StatusAsync(post, "/api/v1/users", new CreateUserRequest("lecteur", "lecteur-mot-de-passe", UserRole.Viewer)) == HttpStatusCode.Created, "Administrators create users.");
        Check(await admin.StatusAsync(post, "/api/v1/users", new CreateUserRequest("LECTEUR", "lecteur-mot-de-passe", UserRole.Viewer)) == HttpStatusCode.Conflict, "User names are unique regardless of case.");
        Check(await viewer.LoginAsync("Lecteur", "lecteur-mot-de-passe") == HttpStatusCode.OK, "Login ignores the case of the user name.");
        Check(await viewer.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.OK, "A viewer reads.");
        Check(await viewer.StatusAsync(post, "/api/v1/libraries", new { name = "interdit" }) == HttpStatusCode.Forbidden, "A viewer cannot create.");
        Check(await viewer.StatusAsync(delete, "/api/v1/libraries/" + Guid.NewGuid()) == HttpStatusCode.Forbidden, "A viewer cannot delete.");
        Check(await viewer.StatusAsync(get, "/api/v1/users") == HttpStatusCode.Forbidden, "A viewer cannot list users.");
        Check(await viewer.StatusAsync(get, "/api/v1/node/mode") == HttpStatusCode.OK, "A viewer reads the node mode.");
        Check(await viewer.StatusAsync(put, "/api/v1/node/mode", new { server = false, worker = true }) == HttpStatusCode.Forbidden, "A viewer cannot change the node mode.");
        Check(await viewer.StatusAsync(post, "/api/v1/nodes/enrollment-requests/approve", new ApproveNodeEnrollmentRequest("0123456789ABCDEF")) == HttpStatusCode.Forbidden, "A viewer cannot approve a node.");
        Check(await viewer.StatusAsync(put, "/api/v1/nodes/enrollment-window", new OpenEnrollmentWindowRequest(5)) == HttpStatusCode.Forbidden, "A viewer cannot open enrollment.");
        Check(await viewer.StatusAsync(post, "/api/v1/plugins/base/connection-test", new { }) == HttpStatusCode.Forbidden, "A viewer cannot make the server call external services.");
        var validation = await viewer.StatusAsync(post, "/api/v1/flows/validate", JsonDocument.Parse("{}").RootElement);
        Check(validation is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest or HttpStatusCode.OK, "Read-only POST operations stay available to viewers: " + validation);
        Check(await viewer.StatusAsync(put, "/api/v1/auth/password", new ChangePasswordRequest("mauvais-actuel", "nouveau-mot-de-passe")) == HttpStatusCode.BadRequest, "Changing a password requires the current one.");
        Check(await viewer.StatusAsync(put, "/api/v1/auth/password", new ChangePasswordRequest("lecteur-mot-de-passe", "nouveau-mot-de-passe")) == HttpStatusCode.NoContent, "A viewer changes their own password.");
        Check(await new Client(factory.CreateClient()).LoginAsync("lecteur", "lecteur-mot-de-passe") == HttpStatusCode.Unauthorized, "The old password stops working.");

        // Verrouillage progressif, sans énumération des comptes.
        Check(await admin.StatusAsync(post, "/api/v1/users", new CreateUserRequest("cible", "cible-mot-de-passe-1", UserRole.Viewer)) == HttpStatusCode.Created, "Create a throttling target.");
        var target = new Client(factory.CreateClient());
        for (var attempt = 0; attempt < 5; attempt++)
            Check(await target.LoginAsync("cible", "mauvais mot de passe") == HttpStatusCode.Unauthorized, "A wrong password is refused.");
        Check(await target.LoginAsync("cible", "cible-mot-de-passe-1") == HttpStatusCode.Unauthorized, "Repeated failures lock the account even for the right password.");
        Check(await target.LoginAsync("inconnu", "mauvais mot de passe") == HttpStatusCode.Unauthorized, "Unknown accounts fail identically.");
        // Un administrateur peut débloquer en réinitialisant le mot de passe.
        var users = (await (await admin.SendAsync(get, "/api/v1/users")).Content.ReadFromJsonAsync<List<UserResponse>>(Json))!;
        var viewerUser = users.Single(user => user.Username == "lecteur");
        var targetUser = users.Single(user => user.Username == "cible");
        Check(await admin.StatusAsync(put, $"/api/v1/users/{targetUser.Id}/password", new ResetPasswordRequest("cible-mot-de-passe-2")) == HttpStatusCode.NoContent, "An administrator resets a password.");
        Check(await target.LoginAsync("cible", "cible-mot-de-passe-2") == HttpStatusCode.OK, "A reset clears the lock.");

        // Révocation des sessions.
        Check(await admin.StatusAsync(put, $"/api/v1/users/{targetUser.Id}", new UpdateUserRequest(UserRole.Viewer, false)) == HttpStatusCode.NoContent, "Deactivate a user.");
        Check(await target.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.Unauthorized, "Deactivation revokes live sessions.");
        Check(await new Client(factory.CreateClient()).LoginAsync("cible", "cible-mot-de-passe-2") == HttpStatusCode.Unauthorized, "A deactivated user cannot log in.");
        Check(await viewer.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.OK, "The session of a user who changed password remains valid.");
        Check(await admin.StatusAsync(put, $"/api/v1/users/{viewerUser.Id}", new UpdateUserRequest(UserRole.Admin, true)) == HttpStatusCode.NoContent, "Promote a viewer.");
        Check(await viewer.StatusAsync(post, "/api/v1/libraries", new { name = "promu" }) == HttpStatusCode.Created, "A role change applies to live sessions immediately.");
        Check(await viewer.StatusAsync(post, "/api/v1/auth/logout", csrf: true) == HttpStatusCode.NoContent, "Logout succeeds.");
        Check(await viewer.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.Unauthorized, "A logged out session is dead server-side.");

        // Garde du dernier administrateur.
        var rootId = users.Single(user => user.Username == "root").Id;
        Check(await admin.StatusAsync(put, $"/api/v1/users/{rootId}", new UpdateUserRequest(UserRole.Viewer, true)) == HttpStatusCode.Conflict, "You cannot demote yourself.");
        Check(await admin.StatusAsync(delete, $"/api/v1/users/{rootId}") == HttpStatusCode.Conflict, "You cannot delete yourself.");
        var repository = factory.Services.GetRequiredService<UserRepository>();
        Check(await repository.UpdateAsync(viewerUser.Id, UserRole.Viewer, true, CancellationToken.None) == UserChangeResult.Updated, "Demote the second administrator.");
        Check(await repository.DeleteAsync(rootId, CancellationToken.None) == UserChangeResult.LastAdministrator, "The last administrator cannot be removed.");
        Check(await repository.UpdateAsync(rootId, UserRole.Admin, false, CancellationToken.None) == UserChangeResult.LastAdministrator, "The last administrator cannot be deactivated.");
        Console.WriteLine("PASS: setup, roles, CSRF header, lockout, session revocation and last-administrator guard over HTTP.");

        await PairingAsync(factory, anonymous, admin, viewer);
    }

    private static async Task PairingAsync(WebApplicationFactory<ApiAssembly> factory, Client anonymous, Client admin, Client viewer)
    {
        var get = HttpMethod.Get; var post = HttpMethod.Post; var put = HttpMethod.Put; var delete = HttpMethod.Delete;
        Check(await viewer.LoginAsync("lecteur", "nouveau-mot-de-passe") == HttpStatusCode.OK, "Viewer logs in again.");

        // Enrôlement direct : fermé par défaut.
        var first = NewNode(Guid.NewGuid(), NodeToken());
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enroll", first, csrf: false) == HttpStatusCode.Forbidden, "Direct enrollment of a new identity is closed by default.");
        Check(await anonymous.StatusAsync(put, "/api/v1/nodes/enrollment-window", new OpenEnrollmentWindowRequest(5), csrf: false) == HttpStatusCode.Unauthorized, "Anonymous callers cannot open the window.");
        Check(await admin.StatusAsync(put, "/api/v1/nodes/enrollment-window", new OpenEnrollmentWindowRequest(0)) == HttpStatusCode.BadRequest, "The window duration is bounded.");
        Check(await admin.StatusAsync(put, "/api/v1/nodes/enrollment-window", new OpenEnrollmentWindowRequest(5)) == HttpStatusCode.OK, "An administrator opens the window.");
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enroll", first, csrf: false) == HttpStatusCode.OK, "A new identity enrolls while the window is open.");
        var stolen = first with { NodeToken = NodeToken() };
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enroll", stolen, csrf: false) == HttpStatusCode.Conflict, "Another secret cannot take over an enrolled identity, even with the window open.");
        Check(await admin.StatusAsync(delete, "/api/v1/nodes/enrollment-window") == HttpStatusCode.NoContent, "An administrator closes the window.");
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enroll", first, csrf: false) == HttpStatusCode.OK, "The same identity can repeat its enrollment after the window closes.");
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enroll", stolen, csrf: false) == HttpStatusCode.Forbidden, "A different secret is refused once the window is closed.");
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enroll", NewNode(Guid.NewGuid(), NodeToken()), csrf: false) == HttpStatusCode.Forbidden, "Another new identity is refused once the window is closed.");
        var window = (await (await admin.SendAsync(get, "/api/v1/nodes/enrollment-window")).Content.ReadFromJsonAsync<EnrollmentWindowResponse>(Json))!;
        Check(!window.IsOpen, "The window reports closed.");

        // Les workers gardent leur propre authentification, sans session.
        var token = first.NodeToken;
        var heartbeat = new NodeHeartbeatRequest(first.Name, first.Roles, 1, first.Capabilities);
        Check(await anonymous.StatusAsync(put, $"/api/v1/nodes/{first.Id}/heartbeat", heartbeat, csrf: false, nodeToken: token) == HttpStatusCode.OK, "A worker heartbeats with its token only.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/nodes/{first.Id}/heartbeat", heartbeat, csrf: false, nodeToken: NodeToken()) == HttpStatusCode.Unauthorized, "A heartbeat with a wrong token is refused.");
        Check(await anonymous.StatusAsync(get, $"/api/v1/runtime/nodes/{first.Id}/capacity", nodeToken: token) == HttpStatusCode.OK, "Runtime routes authenticate by node token.");
        Check(await anonymous.StatusAsync(get, $"/api/v1/runtime/nodes/{first.Id}/capacity") == HttpStatusCode.Unauthorized, "Runtime routes refuse requests without a token.");
        var execution = $"/api/v1/executions/{Guid.NewGuid()}";
        Check(await anonymous.StatusAsync(get, execution, nodeToken: token) == HttpStatusCode.NotFound, "A worker may read the state of an execution (authorized, then not found).");
        Check(await anonymous.StatusAsync(get, execution, nodeToken: NodeToken()) == HttpStatusCode.Unauthorized, "An unknown node token cannot read executions.");
        Check(await anonymous.StatusAsync(get, "/api/v1/executions", nodeToken: token) == HttpStatusCode.Unauthorized, "A node token opens no other management route.");
        Check(await anonymous.StatusAsync(post, $"/api/v1/executions/{Guid.NewGuid()}/cancel", csrf: false, nodeToken: token) == HttpStatusCode.Unauthorized, "A node token cannot modify management state.");

        // Jumelage par clé : demandes publiques bornées, approbation réservée aux administrateurs.
        var pairing = new[] { "0123456789ABCDEF", "FEDCBA9876543210", "AAAAAAAAAAAAAAAA" };
        var requests = pairing.Select(key => new CreateNodeEnrollmentRequest(NewNode(Guid.NewGuid(), NodeToken()), key)).ToArray();
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enrollment-requests", requests[0], csrf: false) == HttpStatusCode.Accepted, "A worker can publish a pairing request.");
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enrollment-requests", requests[1], csrf: false) == HttpStatusCode.Accepted, "A second request is accepted.");
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enrollment-requests", requests[2], csrf: false) == HttpStatusCode.TooManyRequests, "Pending pairing requests are capped.");
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enrollment-requests", requests[0], csrf: false) == HttpStatusCode.Accepted, "A pending worker can refresh its own request at the cap.");
        Check(await anonymous.StatusAsync(get, "/api/v1/nodes/enrollment-requests") == HttpStatusCode.Unauthorized, "Pending requests are not public.");
        Check(await anonymous.StatusAsync(post, "/api/v1/nodes/enrollment-requests/approve", new ApproveNodeEnrollmentRequest(pairing[0]), csrf: false) == HttpStatusCode.Unauthorized, "Approval by key needs a session.");
        Check(await anonymous.StatusAsync(post, $"/api/v1/nodes/enrollment-requests/{requests[0].Node.Id}/approve", csrf: false) == HttpStatusCode.Unauthorized, "Approval by identifier needs a session.");
        Check(await viewer.StatusAsync(post, $"/api/v1/nodes/enrollment-requests/{requests[0].Node.Id}/approve") == HttpStatusCode.Forbidden, "Viewers cannot approve.");
        Check(await viewer.StatusAsync(get, "/api/v1/nodes/enrollment-requests") == HttpStatusCode.OK, "Viewers can see pending requests.");
        Check(await admin.StatusAsync(post, "/api/v1/nodes/enrollment-requests/approve", new ApproveNodeEnrollmentRequest(pairing[0])) == HttpStatusCode.OK, "An administrator approves by key.");
        Check(await admin.StatusAsync(post, $"/api/v1/nodes/enrollment-requests/{requests[1].Node.Id}/approve") == HttpStatusCode.OK, "An administrator approves by identifier.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/nodes/{requests[0].Node.Id}/heartbeat", new NodeHeartbeatRequest(requests[0].Node.Name, requests[0].Node.Roles, 1, requests[0].Node.Capabilities), csrf: false, nodeToken: requests[0].Node.NodeToken) == HttpStatusCode.OK,
            "A paired worker heartbeats with the secret it generated.");

        // Changement de mode à distance : route fermée par défaut, demande enregistrée puis livrée par le heartbeat.
        var mode = $"/api/v1/nodes/{first.Id}/mode";
        Check(await anonymous.StatusAsync(put, mode, new ChangeNodeModeRequest(true, true), csrf: false) == HttpStatusCode.Unauthorized, "Anonymous callers cannot change a node mode.");
        Check(await anonymous.StatusAsync(put, mode, new ChangeNodeModeRequest(true, true), csrf: false, nodeToken: token) == HttpStatusCode.Unauthorized, "A node token cannot change a node mode.");
        Check(await viewer.StatusAsync(put, mode, new ChangeNodeModeRequest(true, true)) == HttpStatusCode.Forbidden, "A viewer cannot change a node mode.");
        Check(await admin.StatusAsync(put, mode, new ChangeNodeModeRequest(false, false)) == HttpStatusCode.BadRequest, "A node keeps at least one function.");
        Check(await admin.StatusAsync(put, $"/api/v1/nodes/{Guid.NewGuid()}/mode", new ChangeNodeModeRequest(true, true)) == HttpStatusCode.NotFound, "An unknown node is reported.");
        async Task<NodeResponse?> Beat() => await (await anonymous.SendAsync(put, $"/api/v1/nodes/{first.Id}/heartbeat", heartbeat, csrf: false, nodeToken: token)).Content.ReadFromJsonAsync<NodeResponse>(Json);
        Check((await Beat())?.DesiredRoles is null, "Without a request, the heartbeat carries no desired mode.");
        var current = first.Roles.Contains(NodeRole.Server);
        var otherServer = !current;
        Check(await admin.StatusAsync(put, mode, new ChangeNodeModeRequest(!current, true)) == HttpStatusCode.Accepted, "An administrator requests a mode for a node.");
        var delivered = (await Beat())?.DesiredRoles;
        Check(delivered is not null && delivered.Contains(NodeRole.Worker) && delivered.Contains(NodeRole.Server) == otherServer, "The heartbeat answer delivers the desired mode.");
        Check(await admin.StatusAsync(put, mode, new ChangeNodeModeRequest(current, first.Roles.Contains(NodeRole.Worker))) == HttpStatusCode.Accepted, "Asking for the current mode cancels the request.");
        Check((await Beat())?.DesiredRoles is null, "A cancelled request is not delivered.");
        var applied = new NodeHeartbeatRequest(first.Name, new[] { NodeRole.Worker, NodeRole.Server }, 1, first.Capabilities);
        Check(await admin.StatusAsync(put, mode, new ChangeNodeModeRequest(true, true)) == HttpStatusCode.Accepted, "A request for both functions is accepted.");
        using (var ack = await anonymous.SendAsync(put, $"/api/v1/nodes/{first.Id}/heartbeat", applied, csrf: false, nodeToken: token))
            Check((await ack.Content.ReadFromJsonAsync<NodeResponse>(Json))?.DesiredRoles is null, "Reporting the requested roles clears the request.");
        Check(await admin.StatusAsync(put, mode, new ChangeNodeModeRequest(false, true)) == HttpStatusCode.Conflict, "The last online server cannot lose its server function.");
        Console.WriteLine("PASS: enrollment window, capped pairing requests, admin-only approval and node-token isolation.");
    }

    private static Task EndpointCatalogueAsync(WebApplicationFactory<ApiAssembly> factory)
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        string Describe(RouteEndpoint endpoint) =>
            (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "*") + " " + endpoint.RoutePattern.RawText;
        var anonymous = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null).Select(Describe).Order().ToArray();
        var expected = new[]
        {
            "GET {*path:nonfile}",
            "GET /api/v1/auth/status",
            "GET /api/v1/cluster",
            "GET /health/live",
            "* /health/ready",
            "POST /api/v1/auth/login",
            "POST /api/v1/auth/setup",
            "POST /api/v1/integrations/{pluginId}/webhook",
            "POST /api/v1/nodes/enroll",
            "POST /api/v1/nodes/enrollment-requests",
            "PUT /api/v1/nodes/{nodeId:guid}/heartbeat",
        };
        var runtime = anonymous.Where(item => item.Contains("/api/v1/runtime/nodes/{nodeId:guid}")).ToArray();
        var rest = anonymous.Except(runtime).ToArray();
        Check(runtime.Length > 10, "The runtime group is self-authenticated by node token.");
        Check(rest.SequenceEqual(expected.Order()), "Only these routes may skip user authentication; update the list deliberately:\n" + string.Join("\n", rest));
        Console.WriteLine("PASS: the anonymous route catalogue matches the reviewed list.");
        return Task.CompletedTask;
    }
}
