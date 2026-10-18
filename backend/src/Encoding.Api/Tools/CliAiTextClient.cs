using System.Diagnostics;
using System.Text;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class CliAiTextClient(string toolId, string credentialsBaseDirectory) : IAiTextClient
{
    public async Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default)
    {
        var execName = toolId switch
        {
            "claude-cli" => "claude",
            "codex-cli" => "codex",
            _ => null
        };

        var path = execName is not null ? FindExecutable(execName) : null;
        if (path is null)
        {
            // Mode bouchonné/simulé lorsque la CLI n'est pas installée sur la machine hôte
            return SimulateResponse(prompt);
        }

        try
        {
            var combinedPrompt = $"{prompt.SystemPrompt}\n\n{prompt.UserPrompt}";
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (toolId == "claude-cli")
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(combinedPrompt);
                psi.ArgumentList.Add("--output-format");
                psi.ArgumentList.Add("text");
            }
            else
            {
                psi.ArgumentList.Add("--non-interactive");
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(combinedPrompt);
                psi.ArgumentList.Add("--output-format");
                psi.ArgumentList.Add("text");
            }

            psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(credentialsBaseDirectory, toolId);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Impossible de démarrer {execName}.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            await proc.WaitForExitAsync(cts.Token);

            var stdout = AnsiSanitizer.Sanitize(await stdoutTask);
            var stderr = AnsiSanitizer.Sanitize(await stderrTask);

            if (AnsiSanitizer.DetectRateLimit(stdout) || AnsiSanitizer.DetectRateLimit(stderr))
            {
                return new AiCompletionResult(false, string.Empty, "Limite de quota temporaire atteinte.", IsRateLimited: true);
            }

            if (proc.ExitCode != 0)
            {
                return new AiCompletionResult(false, string.Empty, string.IsNullOrWhiteSpace(stderr) ? $"Erreur code {proc.ExitCode}" : stderr);
            }

            return new AiCompletionResult(true, stdout);
        }
        catch (OperationCanceledException)
        {
            return new AiCompletionResult(false, string.Empty, "Délai d'attente dépassé (timeout 120s).");
        }
        catch (Exception ex)
        {
            return new AiCompletionResult(false, string.Empty, ex.Message);
        }
    }

    private static string? FindExecutable(string executable)
    {
        var name = OperatingSystem.IsWindows() ? executable + ".exe" : executable;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
    }

    private static AiCompletionResult SimulateResponse(AiPrompt prompt)
    {
        // Simulation pour tests locaux sans binaire externe
        if (prompt.UserPrompt.Contains("SRT", StringComparison.OrdinalIgnoreCase))
        {
            return new AiCompletionResult(true, "1\n00:00:01,000 --> 00:00:04,000\n[Traduction simulée]\n");
        }

        return new AiCompletionResult(true, "Réponse IA générée.");
    }
}
