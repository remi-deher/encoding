using System.Net;
using System.Net.Http.Json;
using Encoding.Api.Nodes;
using Encoding.Core.Nodes;
using Encoding.NodeAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LocalAgentAccessTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("http://192.168.1.50:8090")]
    [InlineData("https://192.168.1.50:8090")]
    public async Task RelayCanAddAClusterWithoutDependingOnBrowserOrigin(string? origin)
    {
        using var fixture = new SecretFile();
        var clusterId = Guid.NewGuid();
        var clusterBuilder = WebApplication.CreateBuilder();
        clusterBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cluster = clusterBuilder.Build();
        cluster.MapGet("/api/v1/cluster", () => Results.Ok(new { id = clusterId, name = "Maison" }));
        await cluster.StartAsync();
        await using var agent = Agent(fixture.Path);
        using var relay = new HttpClient(new LocalAgentAuthenticationHandler(fixture.Configuration)
        {
            InnerHandler = agent.Server.CreateHandler()
        }) { BaseAddress = new Uri("http://localhost/") };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/worker/clusters")
        {
            Content = JsonContent.Create(new { apiBaseUrl = cluster.Urls.Single() })
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await relay.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(clusterId.ToString("D"), await response.Content.ReadAsStringAsync());
        using var clusters = await relay.GetAsync("api/v1/worker/clusters");
        Assert.Equal(HttpStatusCode.OK, clusters.StatusCode);
        Assert.Contains(clusterId.ToString("D"), await clusters.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "status")]
    [InlineData("GET", "clusters")]
    [InlineData("POST", "clusters")]
    [InlineData("DELETE", "clusters/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "enroll")]
    [InlineData("POST", "pairing-key")]
    [InlineData("POST", "leave")]
    [InlineData("GET", "mode")]
    [InlineData("PUT", "mode")]
    public async Task EveryAgentManagementRouteRequiresTheLocalSecret(string method, string path)
    {
        using var fixture = new SecretFile();
        await using var agent = Agent(fixture.Path);
        using var client = agent.CreateClient();
        foreach (var secret in new string?[] { null, "wrong-secret" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "api/v1/worker/" + path);
            request.Headers.Add("Origin", "http://localhost:4200");
            if (secret is not null) request.Headers.Add(LocalEnrollment.HeaderName, secret);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var health = await client.GetAsync("health/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task MissingSecretDoesNotOpenTheAgent()
    {
        await using var agent = Agent(null);
        using var client = agent.CreateClient();
        using var response = await client.GetAsync("api/v1/worker/clusters");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RelayUsesTheCurrentSecretAndNeverSendsItToRemoteAddresses()
    {
        using var fixture = new SecretFile();
        var sentSecrets = new List<string>();
        using var client = new HttpClient(new LocalAgentAuthenticationHandler(fixture.Configuration)
        {
            InnerHandler = new CaptureHandler(sentSecrets)
        });
        using var first = await client.GetAsync("http://127.0.0.1:8081/api/v1/worker/clusters");
        LocalEnrollment.Create(fixture.Path);
        using var second = await client.GetAsync("http://127.0.0.1:8081/api/v1/worker/clusters");
        Assert.NotEqual(sentSecrets[0], sentSecrets[1]);
        Assert.Equal(LocalEnrollment.Read(fixture.Path), sentSecrets[1]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://192.168.1.51:8081/api/v1/worker/clusters"));
        File.Delete(fixture.Path);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:8081/api/v1/worker/clusters"));
        Assert.Equal(2, sentSecrets.Count);
    }

    [Fact]
    public async Task ServerLocalNodeRelayHandles204NoContentOnLeaveAndForgetCluster()
    {
        using var fixture = new SecretFile();
        await using var agent = Agent(fixture.Path);
        var root = Path.Combine(Path.GetTempPath(), "encoding-local-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var server = new WebApplicationFactory<Encoding.Api.Auth.AuthOptions>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
                builder.UseSetting("Logging:LogLevel:Default", "Error");
                builder.UseSetting("ConnectionStrings:Cluster", $"Data Source={Path.Combine(root, "cluster.db")};Pooling=False");
                builder.UseSetting("Database:Provider", "sqlite");
                builder.UseSetting("Node:DataDirectory", root);
                builder.UseSetting("Auth:Enabled", "false");
                builder.UseSetting(LocalEnrollment.SecretFileSetting, fixture.Path);
                builder.ConfigureServices(services =>
                {
                    foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray())
                        services.Remove(descriptor);
                    services.AddHttpClient(LocalNodeEndpoints.HttpClientName, client =>
                    {
                        client.BaseAddress = new Uri("http://127.0.0.1:8081/");
                    }).ConfigurePrimaryHttpMessageHandler(() => new LocalAgentAuthenticationHandler(fixture.Configuration)
                    {
                        InnerHandler = agent.Server.CreateHandler()
                    });
                });
            });

            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Add(Encoding.Api.Auth.ManagementAccessHandler.CsrfHeader, "1");
            using var leaveResponse = await client.PostAsync("/api/v1/local-node/leave", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NoContent, leaveResponse.StatusCode);
            Assert.Empty(await leaveResponse.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WebApplicationFactory<NodeAgentOptions> Agent(string? secretFile) =>
        new WebApplicationFactory<NodeAgentOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            builder.UseSetting(LocalEnrollment.SecretFileSetting, secretFile ?? "");
            if (secretFile is not null)
                builder.UseSetting("Node:DataDirectory", System.IO.Path.Combine(System.IO.Path.GetDirectoryName(secretFile)!, "node"));
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(service);
            });
        });

    private sealed class CaptureHandler(List<string> secrets) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            secrets.Add(Assert.Single(request.Headers.GetValues(LocalEnrollment.HeaderName)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class SecretFile : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "encoding-agent-auth-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public IConfiguration Configuration { get; }
        public SecretFile()
        {
            Path = System.IO.Path.Combine(directory, LocalEnrollment.FileName);
            LocalEnrollment.Create(Path);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LocalEnrollment.SecretFileSetting] = Path
            }).Build();
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
