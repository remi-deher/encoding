using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class AiClientFactory(IConfiguration configuration, IHttpClientFactory httpClientFactory)
{
    public IAiTextClient CreateClient(string? preferredProvider = null)
    {
        var provider = preferredProvider
            ?? configuration["AiTools:DefaultProvider"]
            ?? "codex-cli";

        var credDir = configuration["AiTools:CredentialsDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "Data", "credentials", "ai-tools");

        if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            var url = configuration["AiTools:Ollama:BaseUrl"] ?? "http://127.0.0.1:11434/v1/";
            var model = configuration["AiTools:Ollama:Model"] ?? "llama3.2";
            var client = httpClientFactory.CreateClient("AiClient");
            client.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
            return new OpenAiCompatibleAiClient(client, model);
        }

        if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            var url = configuration["AiTools:OpenAi:BaseUrl"] ?? "https://api.openai.com/v1/";
            var model = configuration["AiTools:OpenAi:Model"] ?? "gpt-4o-mini";
            var key = configuration["AiTools:OpenAi:ApiKey"];
            var client = httpClientFactory.CreateClient("AiClient");
            client.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
            return new OpenAiCompatibleAiClient(client, model, key);
        }

        // Par défaut : CLI officielle (Codex ou Claude)
        var toolId = provider.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) || provider.Equals("claude", StringComparison.OrdinalIgnoreCase)
            ? "claude-cli"
            : "codex-cli";

        return new CliAiTextClient(toolId, credDir);
    }
}
