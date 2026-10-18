using Encoding.Api.Flows;
using Encoding.Api.Tools;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Tools;
using Encoding.NodeAgent.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiToolTests
{
    [Fact]
    public void HardwareCapabilities_Protects_Accelerators()
    {
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["nvenc"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["vaapi"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["qsv"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["cuda"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(["ai-text-generation"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(["subtitles-translation"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(null));
    }

    [Fact]
    public void ScriptTools_Includes_AiCliTools()
    {
        Assert.Contains("codex", ScriptTools.AllowedTools);
        Assert.Contains("claude", ScriptTools.AllowedTools);
    }

    [Fact]
    public void DeviceCodeAuthService_Lifecycle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "enc_ai_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiTools:CredentialsDirectory"] = tempDir
                })
                .Build();

            var service = new DeviceCodeAuthService(config);

            // 1. Initial status: unconfigured
            var status = service.GetStatus("codex-cli");
            Assert.Equal("unconfigured", status.State);

            // 2. Start device auth
            var start = service.StartDeviceAuth("codex-cli");
            Assert.Equal("codex-cli", start.ToolId);
            Assert.False(string.IsNullOrWhiteSpace(start.Prompt.UserCode));
            Assert.Contains("chatgpt.com", start.Prompt.VerificationUri);

            // 3. Status is now pending
            var pendingStatus = service.GetStatus("codex-cli");
            Assert.Equal("pending", pendingStatus.State);

            // 4. Complete authorization
            var completed = service.CompleteAuthorization("codex-cli", "mon-compte-chatgpt");
            Assert.True(completed);

            // 5. Status is now authorized
            var authStatus = service.GetStatus("codex-cli");
            Assert.Equal("authorized", authStatus.State);
            Assert.Equal("mon-compte-chatgpt", authStatus.AccountName);

            // 6. List tools shows authorized
            var tools = service.ListTools();
            var codex = tools.Single(t => t.Id == "codex-cli");
            Assert.Equal("authorized", codex.AuthStatus.State);

            // 7. Revoke
            var revoked = service.RevokeAuth("codex-cli");
            Assert.True(revoked);
            var afterRevoke = service.GetStatus("codex-cli");
            Assert.Equal("unconfigured", afterRevoke.State);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AnsiSanitizer_StripsCodes_And_DetectsRateLimits()
    {
        var rawWithAnsi = "\x1B[32mSuccès!\x1B[0m\r\nTexte propre.";
        var sanitized = AnsiSanitizer.Sanitize(rawWithAnsi);
        Assert.Equal("Succès!\nTexte propre.", sanitized);

        Assert.True(AnsiSanitizer.DetectRateLimit("Error: rate limit reached, please try again in 30s."));
        Assert.True(AnsiSanitizer.DetectRateLimit("HTTP 429 Too Many Requests"));
        Assert.False(AnsiSanitizer.DetectRateLimit("Traduction effectuée avec succès."));
    }

    [Fact]
    public async Task CliAiTextClient_SimulatedResponse_WhenExecutableMissing()
    {
        var client = new CliAiTextClient("mock-ai-cli", Path.GetTempPath());
        var res = await client.GenerateTextAsync(new AiPrompt("Système", "Traduire ce SRT : 1\n00:00:01,000 --> 00:00:04,000\nHello\n"));
        Assert.True(res.Success);
        Assert.Contains("Traduction", res.Content);
    }

    [Fact]
    public async Task FlowAiGeneratorService_Generates_Valid_Flow()
    {
        var config = new ConfigurationBuilder().Build();
        var registry = new FlowComponentRegistry(config);
        var validator = new FlowDefinitionValidator(registry);
        var promptBuilder = new FlowCatalogPromptBuilder(registry);
        var factory = new AiClientFactory(config, new MockHttpClientFactory());
        var service = new FlowAiGeneratorService(factory, promptBuilder, validator, registry);

        var promptText = promptBuilder.BuildSystemPrompt("video");
        Assert.Contains("base-transcode-video", promptText);
        Assert.Contains("RÈGLES D'OR", promptText);

        var response = await service.GenerateFlowAsync(new FlowAiGenerateRequest("video", "Encoder en HEVC avec audio AAC"));
        Assert.True(response.Success);
        Assert.NotNull(response.Flow);
        Assert.Equal("video", response.Flow.MediaType);
        Assert.True(response.Flow.Nodes.Count >= 1);

        var validationResult = validator.Validate(response.Flow);
        Assert.True(validationResult.IsValid);
    }

    [Fact]
    public void SubtitleChunker_Parses_Chunks_And_Reassembles()
    {
        var srt = """
            1
            00:00:01,000 --> 00:00:03,500
            Hello world!

            2
            00:00:04,000 --> 00:00:06,000
            How are you?

            3
            00:00:07,000 --> 00:00:09,000
            I am fine, thank you.
            """;

        var entries = Encoding.Api.Flows.Subtitles.SubtitleChunker.ParseSrt(srt);
        Assert.Equal(3, entries.Count);
        Assert.Equal("Hello world!", entries[0].Text);
        Assert.Equal("00:00:01,000 --> 00:00:03,500", entries[0].Timecode);

        var chunks = Encoding.Api.Flows.Subtitles.SubtitleChunker.ChunkEntries(entries, 2);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(2, chunks[0].Count);
        Assert.Single(chunks[1]);

        var promptChunk = Encoding.Api.Flows.Subtitles.SubtitleChunker.FormatChunkForPrompt(chunks[0]);
        Assert.Contains("Hello world!", promptChunk);
        Assert.Contains("00:00:01,000 --> 00:00:03,500", promptChunk);

        var translated = Encoding.Api.Flows.Subtitles.SubtitleChunker.ReassembleSrt([
            "1\n00:00:01,000 --> 00:00:03,500\nBonjour le monde !\n\n2\n00:00:04,000 --> 00:00:06,000\nComment allez-vous ?",
            "3\n00:00:07,000 --> 00:00:09,000\nJe vais bien, merci."
        ]);

        Assert.Contains("Bonjour le monde !", translated);
        Assert.Contains("Comment allez-vous ?", translated);
        Assert.Contains("Je vais bien, merci.", translated);
    }

    [Fact]
    public void FlowComponentRegistry_Registers_AiComponents()
    {
        var registry = new FlowComponentRegistry(new ConfigurationBuilder().Build());
        var translateComp = registry.FindReference("ai-translate-subtitles");
        Assert.NotNull(translateComp);
        Assert.Equal("ai", translateComp.PluginId);

        var enrichComp = registry.FindReference("ai-metadata-enricher");
        Assert.NotNull(enrichComp);
        Assert.Equal("ai", enrichComp.PluginId);

        // Legacy aliases
        var legacyTranslate = registry.FindReference("base-ai-translate-subtitles");
        Assert.NotNull(legacyTranslate);
        Assert.Equal("ai", legacyTranslate.PluginId);
    }

    [Fact]
    public void FlowComponentRegistry_Loads_Modular_Plugins()
    {
        var registry = new FlowComponentRegistry(new ConfigurationBuilder().Build());

        // Plugins exist
        var pluginIds = registry.Plugins.Select(p => p.Id).ToHashSet();
        Assert.Contains("base", pluginIds);
        Assert.Contains("ai", pluginIds);
        Assert.Contains("mkv", pluginIds);
        Assert.Contains("images", pluginIds);
        Assert.Contains("probe", pluginIds);

        // MKV
        var remux = registry.FindReference("mkv-remux-matroska");
        Assert.NotNull(remux);
        Assert.Equal("mkv", remux.PluginId);
        var legacyRemux = registry.FindReference("base-remux-matroska");
        Assert.NotNull(legacyRemux);
        Assert.Equal("mkv", legacyRemux.PluginId);

        // Images
        var convert = registry.FindReference("image-convert");
        Assert.NotNull(convert);
        Assert.Equal("images", convert.PluginId);
        var legacyConvert = registry.FindReference("base-convert-image");
        Assert.NotNull(legacyConvert);
        Assert.Equal("images", legacyConvert.PluginId);

        // Probe
        var probe = registry.FindReference("probe-media");
        Assert.NotNull(probe);
        Assert.Equal("probe", probe.PluginId);
        var legacyProbe = registry.FindReference("base-probe-media");
        Assert.NotNull(legacyProbe);
        Assert.Equal("probe", legacyProbe.PluginId);
    }

    [Fact]
    public void AiSettingsService_Lifecycle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "enc_ai_settings_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiTools:CredentialsDirectory"] = tempDir,
                    ["AiTools:DefaultProvider"] = "codex-cli"
                })
                .Build();

            var service = new AiSettingsService(config);
            var initial = service.GetSettings();
            Assert.Equal("codex-cli", initial.DefaultProvider);

            var updated = service.UpdateSettings(new AiSettingsUpdateRequest(
                DefaultProvider: "ollama",
                OllamaBaseUrl: "http://192.168.1.100:11434/v1/",
                OllamaModel: "qwen2.5-coder:7b",
                TimeoutSeconds: 60
            ));

            Assert.Equal("ollama", updated.DefaultProvider);
            Assert.Equal("http://192.168.1.100:11434/v1/", updated.OllamaBaseUrl);
            Assert.Equal("qwen2.5-coder:7b", updated.OllamaModel);
            Assert.Equal(60, updated.TimeoutSeconds);

            // Recharger une nouvelle instance depuis le même dossier pour vérifier la persistance
            var reloadedService = new AiSettingsService(config);
            var reloaded = reloadedService.GetSettings();
            Assert.Equal("ollama", reloaded.DefaultProvider);
            Assert.Equal("http://192.168.1.100:11434/v1/", reloaded.OllamaBaseUrl);
            Assert.Equal("qwen2.5-coder:7b", reloaded.OllamaModel);
            Assert.Equal(60, reloaded.TimeoutSeconds);

            // Créer un client depuis la factory
            var factory = new AiClientFactory(config, new MockHttpClientFactory(), reloadedService);
            var client = factory.CreateClient();
            Assert.NotNull(client);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
