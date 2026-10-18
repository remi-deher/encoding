using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed partial class DeviceCodeAuthService
{
    private readonly string credentialsBaseDirectory;
    private readonly ConcurrentDictionary<string, ActiveSession> sessions = new(StringComparer.OrdinalIgnoreCase);

    public DeviceCodeAuthService(IConfiguration configuration)
    {
        var configuredPath = configuration["AiTools:CredentialsDirectory"];
        credentialsBaseDirectory = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, "Data", "credentials", "ai-tools");
        Directory.CreateDirectory(credentialsBaseDirectory);
    }

    public IReadOnlyList<AiToolInfo> ListTools()
    {
        var result = new List<AiToolInfo>();
        foreach (var (id, name, desc, exec, defaultUrl) in new[]
        {
            ("codex-cli", "OpenAI Codex / ChatGPT CLI", "Interface officielle pour ChatGPT / Codex.", "codex", "https://chatgpt.com/auth/device"),
            ("claude-cli", "Anthropic Claude Code CLI", "Interface officielle pour Claude.", "claude", "https://claude.ai/auth")
        })
        {
            var isInstalled = FindExecutable(exec) is not null;
            var status = GetStatus(id);
            result.Add(new AiToolInfo(
                id,
                name,
                desc,
                "ai-text",
                isInstalled,
                status,
                ["ai-text-generation", "subtitles-translation"]));
        }
        return result;
    }

    public DeviceAuthStatus GetStatus(string toolId)
    {
        var credFile = Path.Combine(credentialsBaseDirectory, toolId, "credentials.json");
        if (File.Exists(credFile))
        {
            try
            {
                var content = File.ReadAllText(credFile);
                var doc = JsonSerializer.Deserialize<JsonElement>(content);
                var account = doc.TryGetProperty("account", out var acc) ? acc.GetString() : "Connecté";
                var date = doc.TryGetProperty("authenticatedAt", out var auth) && auth.TryGetDateTimeOffset(out var dt)
                    ? dt
                    : File.GetLastWriteTimeUtc(credFile);
                return new DeviceAuthStatus(toolId, "authorized", account, date);
            }
            catch
            {
                return new DeviceAuthStatus(toolId, "authorized", "Connecté", File.GetLastWriteTimeUtc(credFile));
            }
        }

        if (sessions.TryGetValue(toolId, out var session))
        {
            if (session.ExpiresAt < DateTimeOffset.UtcNow)
            {
                sessions.TryRemove(toolId, out _);
                return new DeviceAuthStatus(toolId, "expired", ErrorMessage: "La session d’authentification a expiré.");
            }
            if (session.IsAuthorized)
            {
                return new DeviceAuthStatus(toolId, "authorized", session.AccountName ?? "Connecté", session.AuthorizedAt);
            }
            if (session.ErrorMessage is not null)
            {
                return new DeviceAuthStatus(toolId, "failed", ErrorMessage: session.ErrorMessage);
            }
            return new DeviceAuthStatus(toolId, "pending");
        }

        return new DeviceAuthStatus(toolId, "unconfigured");
    }

    public DeviceAuthStartResponse StartDeviceAuth(string toolId)
    {
        if (sessions.TryGetValue(toolId, out var existing) && existing.ExpiresAt > DateTimeOffset.UtcNow && !existing.IsAuthorized)
        {
            return new DeviceAuthStartResponse(toolId, existing.Prompt);
        }

        var defaultUrl = toolId switch
        {
            "claude-cli" => "https://claude.ai/auth",
            _ => "https://chatgpt.com/auth/device"
        };

        var userCode = GenerateUserCode();
        var prompt = new DeviceAuthPrompt(userCode, defaultUrl, 900, 5);
        var session = new ActiveSession(toolId, prompt, DateTimeOffset.UtcNow.AddSeconds(900));
        sessions[toolId] = session;

        // Tenter d'exécuter la CLI réelle si présente sur la machine
        var exec = toolId switch
        {
            "claude-cli" => "claude",
            _ => "codex"
        };
        var path = FindExecutable(exec);
        if (path is not null)
        {
            _ = Task.Run(() => RunCliLoginProcessAsync(toolId, path, session));
        }

        return new DeviceAuthStartResponse(toolId, prompt);
    }

    public bool CompleteAuthorization(string toolId, string? accountName = null)
    {
        var targetDir = Path.Combine(credentialsBaseDirectory, toolId);
        Directory.CreateDirectory(targetDir);
        var credFile = Path.Combine(targetDir, "credentials.json");

        var account = accountName ?? (toolId == "claude-cli" ? "Compte Claude" : "Compte ChatGPT");
        var now = DateTimeOffset.UtcNow;
        var data = new
        {
            toolId,
            account,
            authenticatedAt = now
        };
        File.WriteAllText(credFile, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));

        if (sessions.TryGetValue(toolId, out var session))
        {
            session.IsAuthorized = true;
            session.AccountName = account;
            session.AuthorizedAt = now;
        }

        return true;
    }

    public bool RevokeAuth(string toolId)
    {
        sessions.TryRemove(toolId, out _);
        var targetDir = Path.Combine(credentialsBaseDirectory, toolId);
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, true);
            return true;
        }
        return false;
    }

    private async Task RunCliLoginProcessAsync(string toolId, string executablePath, ActiveSession session)
    {
        try
        {
            var args = toolId == "claude-cli" ? ["auth", "login"] : new[] { "auth", "login", "--device-code" };
            var psi = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(credentialsBaseDirectory, toolId);

            using var proc = Process.Start(psi);
            if (proc is null) return;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            await Task.WhenAny(Task.Delay(TimeSpan.FromMinutes(15)), proc.WaitForExitAsync());
            if (proc.HasExited && proc.ExitCode == 0)
            {
                CompleteAuthorization(toolId);
            }
        }
        catch (Exception ex)
        {
            session.ErrorMessage = ex.Message;
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

    private static string GenerateUserCode()
    {
        var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var part1 = Random.Shared.GetItems(chars.ToCharArray(), 4);
        var part2 = Random.Shared.GetItems(chars.ToCharArray(), 4);
        return $"{new string(part1)}-{new string(part2)}";
    }

    private sealed class ActiveSession(string toolId, DeviceAuthPrompt prompt, DateTimeOffset expiresAt)
    {
        public string ToolId { get; } = toolId;
        public DeviceAuthPrompt Prompt { get; } = prompt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool IsAuthorized { get; set; }
        public string? AccountName { get; set; }
        public DateTimeOffset? AuthorizedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
