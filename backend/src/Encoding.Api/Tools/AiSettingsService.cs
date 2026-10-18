using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class AiSettingsService
{
    private readonly string settingsFilePath;
    private readonly object syncRoot = new();
    private AiSettingsDto cachedSettings;

    public AiSettingsService(IConfiguration configuration)
    {
        var credDir = configuration["AiTools:CredentialsDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "Data", "credentials", "ai-tools");

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
        lock (syncRoot)
        {
            cachedSettings = new AiSettingsDto(
                DefaultProvider: !string.IsNullOrWhiteSpace(update.DefaultProvider) ? update.DefaultProvider : cachedSettings.DefaultProvider,
                OllamaBaseUrl: !string.IsNullOrWhiteSpace(update.OllamaBaseUrl) ? update.OllamaBaseUrl : cachedSettings.OllamaBaseUrl,
                OllamaModel: !string.IsNullOrWhiteSpace(update.OllamaModel) ? update.OllamaModel : cachedSettings.OllamaModel,
                OpenAiBaseUrl: !string.IsNullOrWhiteSpace(update.OpenAiBaseUrl) ? update.OpenAiBaseUrl : cachedSettings.OpenAiBaseUrl,
                OpenAiModel: !string.IsNullOrWhiteSpace(update.OpenAiModel) ? update.OpenAiModel : cachedSettings.OpenAiModel,
                OpenAiApiKey: update.OpenAiApiKey ?? cachedSettings.OpenAiApiKey,
                TimeoutSeconds: update.TimeoutSeconds is > 0 ? update.TimeoutSeconds.Value : cachedSettings.TimeoutSeconds,
                SubtitleBatchSize: update.SubtitleBatchSize is > 0 ? update.SubtitleBatchSize.Value : cachedSettings.SubtitleBatchSize
            );

            SaveSettings(cachedSettings);
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
                if (loaded is not null) return loaded;
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
        try
        {
            var dir = Path.GetDirectoryName(settingsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            });

            var tmp = settingsFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, settingsFilePath, overwrite: true);
        }
        catch
        {
            // Erreur d'écriture ignorée pour ne pas planter le runtime
        }
    }
}
