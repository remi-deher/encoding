using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Tests de cluster avec de vrais conteneurs de l'image `encoding` (réseau Docker, utilisateur non-root, lanceur).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute(bool needsPreviousImage = false)
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_DOCKER") != "1")
            Skip = "Définir ENCODING_TEST_DOCKER=1 et ENCODING_TEST_IMAGE (image construite) ; Docker, Linux et ffmpeg requis.";
        else if (!OperatingSystem.IsLinux() || Encoding.NodeAgent.Runtime.ProcessToolRunner.Find("ffmpeg") is null || Encoding.NodeAgent.Runtime.ProcessToolRunner.Find("docker") is null)
            Skip = "Linux, docker et ffmpeg sont requis.";
        else if (needsPreviousImage && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ENCODING_TEST_PREVIOUS_IMAGE")))
            Skip = "Définir ENCODING_TEST_PREVIOUS_IMAGE (ex. mrcryllix/encoding:latest, la dernière version publiée).";
    }
}

[Collection("peer-operations")]
public sealed class DockerClusterTests
{
    private static string Image => Environment.GetEnvironmentVariable("ENCODING_TEST_IMAGE") ?? "encoding:test";

    /// <summary>Un cluster éphémère : réseau Docker dédié, un dossier média partagé, un serveur joignable depuis l'hôte.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private readonly string id = Guid.NewGuid().ToString("N")[..8];
        private readonly List<string> containers = [];
        private readonly List<string> dataDirectories = [];
        public string Network => "enc-test-" + id;
        public string Media { get; } = Path.Combine(Path.GetTempPath(), "encoding-docker-" + Guid.NewGuid().ToString("N"));
        public string Data { get; } = Path.Combine(Path.GetTempPath(), "encoding-docker-data-" + Guid.NewGuid().ToString("N"));
        public HttpClient Client { get; private set; } = null!;
        public string ServerName => "server-" + id;

        public async Task StartServerAsync()
        {
            Directory.CreateDirectory(Media);
            PrepareStorePackages(Data);
            dataDirectories.Add(Data);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            await DockerAsync("network", "create", Network);
            var port = FreePort();
            await RunAsync(ServerName, "--hostname", ServerName, "-v", $"{Data}:/var/lib/encoding-node", "-p", $"127.0.0.1:{port}:8080",
                "-e", "Node__DataDirectory=/var/lib/encoding-node",
                "-e", "Node__Roles=server", "-e", "Node__Name=" + ServerName, "-e", $"Cluster__AdvertisedApiBaseUrl=http://{ServerName}:8080/", Image);
            Client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(20) };
            Client.DefaultRequestHeaders.Add("X-Encoding-Csrf", "1");
            await WaitAsync(async () => (await Client.GetAsync("health/live")).IsSuccessStatusCode, 90, "le serveur");
            using var setup = await Client.PostAsJsonAsync("api/v1/auth/setup", new { username = "admin", password = "StrongPassword123!", setupToken = (string?)null, nodeDisplayName = "srv" });
            Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
            using var window = await Client.PutAsJsonAsync("api/v1/nodes/enrollment-window", new { minutes = 30 });
            Assert.True(window.IsSuccessStatusCode);
        }

        public Task StartWorkerAsync(string name, string image)
        {
            var workerData = Data + "-" + name;
            PrepareStorePackages(workerData);
            dataDirectories.Add(workerData);
            return RunAsync(name, "--hostname", name, "-v", $"{workerData}:/var/lib/encoding-node", "-v", $"{Media}:/media/lib",
                "-e", "Node__DataDirectory=/var/lib/encoding-node",
                "-e", "Node__Roles=worker", "-e", "Node__Name=" + name, "-e", "Node__MaxConcurrentTasks=1", "-e", $"Cluster__ApiBaseUrl=http://{ServerName}:8080/",
                "-e", "Worker__AutoAcceptEnrollment=true", image);
        }

        private static void PrepareStorePackages(string dataDirectory)
        {
            var fixtureDirectory = Path.Combine(Harness.RepositoryRoot(), "tests", "Fixtures", "StoreCatalog");
            var storeDirectory = Path.Combine(dataDirectory, "plugin-store");
            var installed = new List<object>();
            foreach (var (id, version) in new[] { ("base", "1.10.0"), ("video", "1.3.0") })
            {
                var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, id + ".json")))!.AsObject();
                manifest["runtime"] = new JsonObject { ["type"] = "native", ["entrypoint"] = "" };
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                using (var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open()))
                    writer.Write(manifest.ToJsonString());
                var bytes = buffer.ToArray();
                var packageDirectory = Path.Combine(storeDirectory, "packages", id, version);
                Directory.CreateDirectory(packageDirectory);
                File.WriteAllBytes(Path.Combine(packageDirectory, "package.zip"), bytes);
                installed.Add(new
                {
                    id,
                    version,
                    name = manifest["name"]!.GetValue<string>(),
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    source = "repo:test",
                    installedAt = DateTimeOffset.UtcNow
                });
            }
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(storeDirectory, "installed.json"),
                JsonSerializer.Serialize(installed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        private async Task RunAsync(string name, params string[] arguments)
        {
            var full = new List<string> { "run", "-d", "--name", $"{name}-{id}".Replace($"-{id}-{id}", $"-{id}"), "--network", Network, "-e", "ASPNETCORE_ENVIRONMENT=Production", "-e", "Auth__CookieSecure=false" };
            full.AddRange(arguments);
            containers.Add($"{name}-{id}".Replace($"-{id}-{id}", $"-{id}"));
            await DockerAsync([.. full]);
        }

        public string ContainerOf(string nodeName) => containers.Single(container => container.StartsWith(nodeName, StringComparison.Ordinal));

        public async Task<string> ProcessesAsync(string container) => await DockerAsync("top", container);

        public async ValueTask DisposeAsync()
        {
            foreach (var container in containers)
            {
                var logs = await DockerAsync(allowFailure: true, "logs", "--tail", "5", container);
                if (Environment.GetEnvironmentVariable("ENCODING_TEST_DOCKER_LOGS") == "1") Console.WriteLine($"[{container}]\n{logs}");
                await DockerAsync(allowFailure: true, "rm", "-f", container);
            }
            await DockerAsync(allowFailure: true, "network", "rm", Network);
            Client?.Dispose();
            try { Directory.Delete(Media, true); } catch { }
            foreach (var directory in dataDirectories)
                try { Directory.Delete(directory, true); } catch { }
        }

        public async Task<string> LogsAsync() => string.Join("\n", await Task.WhenAll(containers.Select(async container => $"[{container}] " + await DockerAsync(allowFailure: true, "logs", "--tail", "15", container))));
    }

    private static Task<string> DockerAsync(params string[] arguments) => DockerAsync(false, arguments);

    private static async Task<string> DockerAsync(bool allowFailure, params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output + await error;
        Assert.True(allowFailure || process.ExitCode == 0, $"docker {string.Join(' ', arguments)} : {text}");
        return text;
    }

    [DockerFact]
    public async Task KillingTheEncodingWorkerContainerResumesOnTheOtherContainerAndKeepsTheSource()
    {
        await using var cluster = new Cluster();
        await cluster.StartServerAsync();
        var source = Path.Combine(cluster.Media, "film.mkv");
        await EncodeFixtureAsync(source, seconds: 90, size: "1280x720");
        var sourceSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        await cluster.StartWorkerAsync("worker-a", Image);
        await cluster.StartWorkerAsync("worker-b", Image);
        var nodes = await WaitForWorkersAsync(cluster, 2, requireStoreCapabilities: true);
        var executionId = await QueueEncodingAsync(cluster, nodes);

        string? victim = null;
        string? executionDiagnostic = null;
        await WaitAsync(async () =>
        {
            var current = (await cluster.Client.GetFromJsonAsync<JsonElement>("api/v1/executions")).GetProperty("executions")[0];
            executionDiagnostic = $"État : {current.GetProperty("state").GetString()} ; nœud : {current.GetProperty("node")} ; détail : {current.GetProperty("detail")}";
            if (current.GetProperty("state").GetString() != "running" || current.GetProperty("node").ValueKind != JsonValueKind.String) return false;
            var name = current.GetProperty("node").GetString()!;
            if (!(await cluster.ProcessesAsync(cluster.ContainerOf(name))).Contains("ffmpeg", StringComparison.Ordinal)) return false;
            victim = name;
            return true;
        }, 240, "ffmpeg dans le conteneur du worker", cluster, () => executionDiagnostic);

        await DockerAsync("kill", cluster.ContainerOf(victim!));   // arrêt brutal du conteneur entier (SIGKILL), sans fermeture propre

        JsonElement final = default;
        await WaitAsync(async () =>
        {
            var list = (await cluster.Client.GetFromJsonAsync<JsonElement>("api/v1/executions")).GetProperty("executions");
            Assert.Equal(1, list.GetArrayLength());
            final = list[0].Clone();
            return final.GetProperty("state").GetString() is "succeeded" or "failed";
        }, 420, "reprise et fin de l'exécution", cluster);

        var history = (await cluster.Client.GetFromJsonAsync<JsonElement>($"api/v1/executions/{executionId}/events")).ToString();
        Assert.True(final.GetProperty("state").GetString() == "succeeded", $"{final.GetProperty("detail")}\n{history}\n{await cluster.LogsAsync()}");
        Assert.NotEqual(victim, final.GetProperty("node").GetString());
        Assert.Contains("reprise", history, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceSha, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
        Assert.DoesNotContain(Directory.GetFiles(cluster.Media), path => path != source);
    }

    [DockerFact(needsPreviousImage: true)]
    public async Task AWorkerFromTheLastPublishedImageJoinsTheNewServerAndCompletesAFlow()
    {
        var previous = Environment.GetEnvironmentVariable("ENCODING_TEST_PREVIOUS_IMAGE")!;
        await using var cluster = new Cluster();
        await cluster.StartServerAsync();
        var source = Path.Combine(cluster.Media, "film.mkv");
        await EncodeFixtureAsync(source, seconds: 6, size: "640x360");
        var sourceSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        await cluster.StartWorkerAsync("worker-old", previous);   // seul worker : c'est l'ancienne version qui doit faire le travail
        var nodes = await WaitForWorkersAsync(cluster, 1);
        var executionId = await QueueEncodingAsync(cluster, nodes);

        JsonElement final = default;
        await WaitAsync(async () =>
        {
            final = (await cluster.Client.GetFromJsonAsync<JsonElement>("api/v1/executions")).GetProperty("executions")[0].Clone();
            return final.GetProperty("state").GetString() is "succeeded" or "failed";
        }, 300, "exécution par le worker de l'ancienne version", cluster);

        var history = (await cluster.Client.GetFromJsonAsync<JsonElement>($"api/v1/executions/{executionId}/events")).ToString();
        Assert.True(final.GetProperty("state").GetString() == "succeeded", $"{final.GetProperty("detail")}\n{history}\n{await cluster.LogsAsync()}");
        Assert.Equal("worker-old", final.GetProperty("node").GetString());
        Assert.Equal(sourceSha, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
    }

    private static async Task<JsonElement> WaitForWorkersAsync(Cluster cluster, int workers, bool requireStoreCapabilities = false)
    {
        JsonElement workersNow = default;
        await WaitAsync(async () =>
        {
            var all = await cluster.Client.GetFromJsonAsync<JsonElement>("api/v1/nodes");
            var online = all.EnumerateArray().Where(node => node.GetProperty("name").GetString()!.StartsWith("worker", StringComparison.Ordinal) && node.GetProperty("status").GetString() == "online").ToArray();
            if (online.Length != workers) return false;
            if (requireStoreCapabilities)
            {
                var requiredCapabilities = new HashSet<string>(["plugin/video", "ffmpeg"], StringComparer.Ordinal);
                if (online.Any(node => !requiredCapabilities.IsSubsetOf(node.GetProperty("capabilities").EnumerateArray()
                        .Select(capability => capability.GetProperty("toolId").GetString()!).ToHashSet(StringComparer.Ordinal)))) return false;
            }
            workersNow = JsonSerializer.SerializeToElement(online);
            return true;
        }, 150, $"{workers} worker(s) en ligne", cluster);
        return workersNow;
    }

    private static async Task<Guid> QueueEncodingAsync(Cluster cluster, JsonElement workers)
    {
        var client = cluster.Client;
        var library = (await PostAsync(client, "api/v1/libraries", new { name = "Docker" })).GetProperty("id").GetGuid();
        using (var access = await client.PutAsJsonAsync($"api/v1/libraries/{library}/node-access", new
        {
            nodes = workers.EnumerateArray().Select(node => new { nodeId = node.GetProperty("id").GetGuid(), mode = "direct", localPath = "/media/lib", canRead = true, canWrite = true, transferSourceNodeId = (Guid?)null, isEnabled = true })
        })) Assert.True(access.IsSuccessStatusCode, await access.Content.ReadAsStringAsync());
        var flow = (await PostAsync(client, "api/v1/flows", new
        {
            name = "Docker", mediaType = "video", publish = true,
            nodes = new object[]
            {
                new { id = "entry", component = "base-input-video", configuration = new { } },
                new { id = "encode", component = "base-transcode-video", configuration = new { processingMode = "direct" } },
                new { id = "end", component = "base-complete-flow", configuration = new { } }
            },
            connections = new object[]
            {
                new { id = "c1", sourceNodeId = "entry", sourceOutputId = "success", targetNodeId = "encode" },
                new { id = "c2", sourceNodeId = "encode", sourceOutputId = "success", targetNodeId = "end" }
            }
        })).GetProperty("definition").GetProperty("id").GetGuid();
        var workflow = await client.GetFromJsonAsync<JsonElement>($"api/v1/libraries/{library}/workflow");
        using (var assign = await client.PutAsJsonAsync($"api/v1/libraries/{library}/flow", new { flowId = flow, version = 1, expectedRevision = workflow.GetProperty("revision").GetInt32() }))
            Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        using (var discover = await client.PostAsync($"api/v1/libraries/{library}/discover", null)) Assert.Equal(HttpStatusCode.Accepted, discover.StatusCode);
        await WaitAsync(async () => (await client.GetFromJsonAsync<JsonElement>($"api/v1/libraries/{library}/workflow")).GetProperty("files").GetArrayLength() == 1, 90, "la découverte du fichier", cluster);
        workflow = await client.GetFromJsonAsync<JsonElement>($"api/v1/libraries/{library}/workflow");
        var queued = await PostAsync(client, $"api/v1/libraries/{library}/executions", new { fileIds = new[] { workflow.GetProperty("files")[0].GetProperty("id").GetGuid() }, assignmentRevision = workflow.GetProperty("revision").GetInt32() });
        Assert.Equal(1, queued.GetProperty("queued").GetInt32());
        return (await client.GetFromJsonAsync<JsonElement>("api/v1/executions")).GetProperty("executions")[0].GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} : {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task EncodeFixtureAsync(string path, int seconds, string size)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=duration={seconds}:size={size}:rate=30", "-f", "lavfi", "-i", $"sine=duration={seconds}:frequency=440", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        // Les conteneurs tournent en non-root : le média de test doit rester lisible et modifiable par eux.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds, string description, Cluster? cluster = null, Func<string?>? diagnostic = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            try { if (await condition()) return; }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Délai dépassé : {description}\n{diagnostic?.Invoke()}\n{(cluster is null ? "" : await cluster.LogsAsync())}");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
