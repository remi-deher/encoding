using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class AiSettingsService
{
    private readonly string settingsFilePath;
    private readonly object syncRoot = new();
    private AiSettingsDto cachedSettings;
    private readonly IDataProtector protector;

    public AiSettingsService(IConfiguration configuration, IDataProtectionProvider? protection = null)
    {
        var credDir = AiCliRuntime.ResolveRoot(configuration);

        protector = (protection ?? DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(credDir, ".keys"))))
            .CreateProtector("Encoding.Ai.ApiKey.v1");
        settingsFilePath = Path.Combine(credDir, "settings.json");
        cachedSettings = LoadSettings(configuration);
    }

    public AiSettingsDto GetSettings()
    {
        lock (syncRoot)
        {
            return cachedSettings;
        }
    }

    public AiSettingsDto UpdateSettings(AiSettingsUpdateRequest update)
    {
        if (update.DefaultProvider is not null && update.DefaultProvider is not ("ollama" or "openai" or "codex-cli" or "claude-cli"))
            throw new ArgumentException("Fournisseur IA inconnu.");
        foreach (var provider in new[] { update.AssistantProvider, update.FlowProvider }.OfType<string>())
            if (provider is not ("default" or "ollama" or "openai" or "codex-cli" or "claude-cli"))
                throw new ArgumentException("Fournisseur IA inconnu.");
        var effectiveAssistantProvider = update.AssistantProvider == "default" ? update.DefaultProvider ?? cachedSettings.DefaultProvider
            : update.AssistantProvider ?? cachedSettings.AssistantProvider ?? update.DefaultProvider ?? cachedSettings.DefaultProvider;
        var effectiveFlowProvider = update.FlowProvider == "default" ? update.DefaultProvider ?? cachedSettings.DefaultProvider
            : update.FlowProvider ?? cachedSettings.FlowProvider ?? update.DefaultProvider ?? cachedSettings.DefaultProvider;
        var assistantLocalOnly = update.AssistantLocalOnly ?? cachedSettings.AssistantLocalOnly;
        var flowLocalOnly = update.FlowLocalOnly ?? cachedSettings.FlowLocalOnly;
        if (assistantLocalOnly && effectiveAssistantProvider != "ollama"
            || flowLocalOnly && effectiveFlowProvider != "ollama")
            throw new ArgumentException("Le mode local uniquement exige Ollama comme fournisseur effectif.");
        if (update.TimeoutSeconds is < 10 or > 600 || update.SubtitleBatchSize is < 5 or > 200)
            throw new ArgumentException("Les limites IA sont invalides.");
        foreach (var url in new[] { update.OllamaBaseUrl, update.OpenAiBaseUrl }.OfType<string>())
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("L’adresse du fournisseur doit être une URL HTTP(S) sans identifiants.");
        lock (syncRoot)
        {
            var next = new AiSettingsDto(
                DefaultProvider: !string.IsNullOrWhiteSpace(update.DefaultProvider) ? update.DefaultProvider : cachedSettings.DefaultProvider,
                OllamaBaseUrl: !string.IsNullOrWhiteSpace(update.OllamaBaseUrl) ? update.OllamaBaseUrl : cachedSettings.OllamaBaseUrl,
                OllamaModel: !string.IsNullOrWhiteSpace(update.OllamaModel) ? update.OllamaModel : cachedSettings.OllamaModel,
                OpenAiBaseUrl: !string.IsNullOrWhiteSpace(update.OpenAiBaseUrl) ? update.OpenAiBaseUrl : cachedSettings.OpenAiBaseUrl,
                OpenAiModel: !string.IsNullOrWhiteSpace(update.OpenAiModel) ? update.OpenAiModel : cachedSettings.OpenAiModel,
                OpenAiApiKey: update.OpenAiApiKey ?? cachedSettings.OpenAiApiKey,
                TimeoutSeconds: update.TimeoutSeconds is > 0 ? update.TimeoutSeconds.Value : cachedSettings.TimeoutSeconds,
                SubtitleBatchSize: update.SubtitleBatchSize is > 0 ? update.SubtitleBatchSize.Value : cachedSettings.SubtitleBatchSize,
                AssistantProvider: update.AssistantProvider is null ? cachedSettings.AssistantProvider : update.AssistantProvider == "default" ? null : update.AssistantProvider,
                FlowProvider: update.FlowProvider is null ? cachedSettings.FlowProvider : update.FlowProvider == "default" ? null : update.FlowProvider,
                AssistantLocalOnly: update.AssistantLocalOnly ?? cachedSettings.AssistantLocalOnly,
                FlowLocalOnly: update.FlowLocalOnly ?? cachedSettings.FlowLocalOnly
            );

            SaveSettings(next);
            cachedSettings = next;
            return cachedSettings;
        }
    }

    private AiSettingsDto LoadSettings(IConfiguration configuration)
    {
        try
        {
            if (File.Exists(settingsFilePath))
            {
                var json = File.ReadAllText(settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AiSettingsDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (loaded is not null) return loaded with { OpenAiApiKey = loaded.OpenAiApiKey is { } key && key.StartsWith("protected:", StringComparison.Ordinal) ? protector.Unprotect(key[10..]) : loaded.OpenAiApiKey };
            }
        }
        catch
        {
            // Repli sur la configuration par défaut
        }

        return new AiSettingsDto(
            DefaultProvider: configuration["AiTools:DefaultProvider"] ?? "codex-cli",
            OllamaBaseUrl: configuration["AiTools:Ollama:BaseUrl"] ?? "http://127.0.0.1:11434/v1/",
            OllamaModel: configuration["AiTools:Ollama:Model"] ?? "llama3.2",
            OpenAiBaseUrl: configuration["AiTools:OpenAi:BaseUrl"] ?? "https://api.openai.com/v1/",
            OpenAiModel: configuration["AiTools:OpenAi:Model"] ?? "gpt-4o-mini",
            OpenAiApiKey: configuration["AiTools:OpenAi:ApiKey"],
            TimeoutSeconds: int.TryParse(configuration["AiTools:TimeoutSeconds"], out var t) && t > 0 ? t : 120,
            SubtitleBatchSize: int.TryParse(configuration["AiTools:SubtitleBatchSize"], out var b) && b > 0 ? b : 80
        );
    }

    private void SaveSettings(AiSettingsDto settings)
    {
        var dir = Path.GetDirectoryName(settingsFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var json = JsonSerializer.Serialize(settings with { OpenAiApiKey = settings.OpenAiApiKey is { Length: > 0 } secret ? "protected:" + protector.Protect(secret) : null }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });

        var tmp = settingsFilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(tmp, options))
        using (var writer = new StreamWriter(stream)) writer.Write(json);
        File.Move(tmp, settingsFilePath, overwrite: true);
    }
}
