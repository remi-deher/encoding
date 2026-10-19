using System.Diagnostics;
using System.Text;
using Encoding.Contracts.Tools;
using Microsoft.AspNetCore.DataProtection;

namespace Encoding.Api.Tools;

public sealed record AiProcessResult(int ExitCode, string Output, string Error);

/// <summary>One isolated, persistent CLI profile per provider on this server. Secrets never leave this profile.</summary>
public class AiCliRuntime(IConfiguration configuration, IDataProtectionProvider protection)
{
    protected virtual bool UseSandbox => OperatingSystem.IsLinux();
    public bool CanRun => !UseSandbox || Encoding.Core.Runtime.ProcessSandbox.IsAvailable;
    private readonly IDataProtector protector = protection.CreateProtector("Encoding.Ai.ClaudeToken.v1");
    public static string ResolveRoot(IConfiguration configuration) => configuration["AiTools:CredentialsDirectory"]
        ?? Path.Combine(configuration["Node:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "Data"), "credentials", "ai-tools");
    public string Root => ResolveRoot(configuration);
    public static string Executable(string id) => id switch
    {
        "codex-cli" => "codex", "claude-cli" => "claude",
        _ => throw new ArgumentException("Fournisseur CLI inconnu.")
    };
    public string Profile(string id)
    {
        _ = Executable(id);
        var path = Path.Combine(Root, id);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    public string? Find(string id)
    {
        var executable = Executable(id);
        var configured = configuration[$"AiTools:Executables:{id}"];
        if (configured is not null) return File.Exists(configured) ? configured : null;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, OperatingSystem.IsWindows() ? executable + ".exe" : executable)).FirstOrDefault(File.Exists);
    }
    public void SaveClaudeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("Jeton Claude invalide.");
        var path = Path.Combine(Profile("claude-cli"), "oauth-token.protected");
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream)) writer.Write(protector.Protect(token));
        File.Move(temporary, path, true);
    }
    public bool HasClaudeToken => File.Exists(Path.Combine(Profile("claude-cli"), "oauth-token.protected"));
    public void DeleteClaudeToken() => File.Delete(Path.Combine(Profile("claude-cli"), "oauth-token.protected"));

    public async Task<AiProcessResult> RunAsync(string id, IReadOnlyList<string> args, CancellationToken ct,
        Action<string>? onOutput = null, string? input = null)
    {
        var start = await CreateStartAsync(id, args, ct);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Démarrage CLI impossible.");
        using var terminate = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = ReadAsync(process.StandardOutput, onOutput, ct);
        var stderr = ReadAsync(process.StandardError, onOutput, ct);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
    /// <summary>Bounded duplex protocol using the same profile, environment and sandbox as inference.</summary>
    public async Task<T> RunProtocolAsync<T>(string id, IReadOnlyList<string> args,
        Func<StreamReader, StreamWriter, CancellationToken, Task<T>> protocol, CancellationToken ct)
    {
        var start = await CreateStartAsync(id, args, ct);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Démarrage CLI impossible.");
        using var terminate = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stderr = ReadAsync(process.StandardError, null, ct);
        try
        {
            var exchange = protocol(process.StandardOutput, process.StandardInput, ct);
            // A noisy or failed stderr reader must also interrupt a blocked protocol.
            var first = await Task.WhenAny(exchange, stderr);
            if (first == stderr && stderr.IsFaulted) await stderr;
            return await exchange;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await stderr; } catch (OperationCanceledException) { }
        }
    }

    private async Task<ProcessStartInfo> CreateStartAsync(string id, IReadOnlyList<string> args, CancellationToken ct)
    {
        var path = Find(id) ?? throw new InvalidOperationException("Outil CLI non installé sur ce nœud.");
        var profile = Profile(id);
        var work = Path.Combine(profile, "work");
        Directory.CreateDirectory(work);
        var prefix = new List<string>();
        var launcher = path;
        if (UseSandbox)
        {
            if (!Encoding.Core.Runtime.ProcessSandbox.IsAvailable) throw new InvalidOperationException("Landlock est requis pour les outils IA.");
            launcher = Environment.ProcessPath ?? "dotnet";
            // Start our own sandbox entrypoint, including when hosted by the test runner.
            if (Path.GetFileNameWithoutExtension(launcher) != "Encoding.Api")
            {
                launcher = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet");
                prefix.Add(typeof(AiCliRuntime).Assembly.Location);
            }
            var runtimeRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            prefix.AddRange(["--isolated-process", profile, path, System.Text.Json.JsonSerializer.Serialize(new[]
            {
                runtimeRoot, configuration["AiTools:RuntimeDirectory"] ?? "/opt/encoding/ai-cli"
            })]);
        }
        var start = new ProcessStartInfo(launcher)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true, WorkingDirectory = work
        };
        foreach (var arg in prefix) start.ArgumentList.Add(arg);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var inherited = start.Environment.ToDictionary(entry => entry.Key, entry => entry.Value, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy", "SYSTEMROOT", "WINDIR", "PATHEXT", "COMSPEC" })
            if (inherited.TryGetValue(name, out var value)) start.Environment[name] = value;
        start.Environment["HOME"] = profile;
        start.Environment["USERPROFILE"] = profile;
        start.Environment["XDG_CONFIG_HOME"] = profile;
        start.Environment[id == "codex-cli" ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR"] = profile;
        var temporaryDirectory = Path.Combine(profile, "tmp");
        Directory.CreateDirectory(temporaryDirectory);
        start.Environment["TMPDIR"] = temporaryDirectory;
        start.Environment["NO_COLOR"] = "1";
        start.Environment["CI"] = "1";
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        start.Environment["DISABLE_TELEMETRY"] = "1";
        start.Environment["DISABLE_ERROR_REPORTING"] = "1";
        if (id == "claude-cli" && HasClaudeToken)
            start.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = protector.Unprotect(await File.ReadAllTextAsync(Path.Combine(profile, "oauth-token.protected"), ct));
        return start;
    }

    private static async Task<string> ReadAsync(StreamReader reader, Action<string>? callback, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is var count && count > 0)
        {
            if (result.Length + count > 1024 * 1024) throw new InvalidOperationException("Sortie CLI trop volumineuse.");
            var chunk = new string(buffer, 0, count);
            result.Append(chunk);
            callback?.Invoke(chunk);
        }
        return result.ToString();
    }
}
