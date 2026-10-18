using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class CliAiTextClient(string toolId, AiCliRuntime runtime, int timeoutSeconds = 120) : IAiTextClient
{
    public async Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default)
    {
        if (runtime.Find(toolId) is null) return new(false, "", "Outil CLI non installé.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var text = prompt.SystemPrompt + "\n\n" + prompt.UserPrompt;
            string[] args = toolId == "claude-cli"
                ? ["-p", "--output-format", "text", "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--disable-slash-commands"]
                : ["exec", "--disable", "shell_tool", "--disable", "unified_exec", "--skip-git-repo-check", "--sandbox", "read-only", "--color", "never", "-c", "approval_policy=\"never\"", "-"];
            if (prompt.Model is { Length: > 0 }) args = [.. args[..^ (toolId == "codex-cli" ? 1 : 0)], "--model", prompt.Model, .. (toolId == "codex-cli" ? new[] { "-" } : Array.Empty<string>())];
            var result = await runtime.RunAsync(toolId, args, timeout.Token, input: text);
            if (AnsiSanitizer.DetectRateLimit(result.Output) || AnsiSanitizer.DetectRateLimit(result.Error))
                return new(false, "", "Quota temporairement atteint.", true);
            if (result.ExitCode != 0) return new(false, "", "L’outil IA a refusé la requête. Vérifiez la connexion et les quotas.");
            return new(true, AnsiSanitizer.Sanitize(result.Output));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "", "Délai maximal de la requête IA dépassé."); }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, "", "L’exécution de l’outil IA a échoué."); }
    }
}
