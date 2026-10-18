using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed partial class DeviceCodeAuthService(AiCliRuntime runtime) : IDisposable
{
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly SemaphoreSlim gate = new(1);

    public async Task<IReadOnlyList<AiToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        var result = new List<AiToolInfo>();
        foreach (var id in new[] { "codex-cli", "claude-cli" })
            result.Add(new(id, id == "codex-cli" ? "Codex CLI" : "Claude Code", "Compte CLI sur ce nœud", "CLI",
                runtime.Find(id) is not null, await GetStatusAsync(id, ct), ["ai-text-generation", "subtitles-translation"]));
        return result;
    }

    public async Task<DeviceAuthStatus> GetStatusAsync(string id, CancellationToken ct = default)
    {
        _ = AiCliRuntime.Executable(id);
        if (sessions.TryGetValue(id, out var session))
        {
            if (session.State == "pending" && DateTimeOffset.UtcNow >= session.ExpiresAt)
            { session.State = "expired"; session.Cancellation.Cancel(); }
            if (session.State != "authorized") return new(id, session.State, ErrorMessage: session.Error);
        }
        if (runtime.Find(id) is null) return new(id, "unconfigured", ErrorMessage: "Outil non installé.");
        if (!runtime.CanRun) return new(id, "failed", ErrorMessage: "Landlock est requis pour exécuter les outils IA sur ce nœud Linux.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var status = await runtime.RunAsync(id, id == "codex-cli" ? ["login", "status"] : ["auth", "status"], timeout.Token);
            if (status.ExitCode != 0) return new(id, "unconfigured");
            if (id == "claude-cli")
            {
                using var doc = JsonDocument.Parse(status.Output);
                if (!doc.RootElement.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.True)
                    return new(id, "unconfigured");
            }
            return new(id, "authorized");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { return new(id, "failed", ErrorMessage: "Impossible de vérifier la connexion CLI."); }
    }

    public async Task<DeviceAuthStartResponse> StartDeviceAuthAsync(string id, CancellationToken ct)
    {
        if (id != "codex-cli") throw new ArgumentException("Claude utilise un jeton généré avec claude setup-token.");
        if (runtime.Find(id) is null) throw new InvalidOperationException("Codex n’est pas installé sur ce nœud.");
        if (!runtime.CanRun) throw new InvalidOperationException("Landlock est requis pour exécuter Codex sur ce nœud Linux.");
        Session session;
        await gate.WaitAsync(ct);
        try
        {
            if (!sessions.TryGetValue(id, out session!) || session.State != "pending" || session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                if (session is not null) session.Cancellation.Cancel();
                session = new();
                sessions[id] = session;
                session.Run = RunLoginAsync(session);
            }
        }
        finally { gate.Release(); }
        try { return new(id, await session.Prompt.Task.WaitAsync(TimeSpan.FromSeconds(30), ct)); }
        catch (TimeoutException)
        {
            session.State = "failed";
            session.Error = "Codex n’a pas fourni de code de connexion.";
            session.Cancellation.Cancel();
            throw new InvalidOperationException(session.Error);
        }
    }

    private async Task RunLoginAsync(Session session)
    {
        try
        {
            var buffer = "";
            var sync = new object();
            var result = await runtime.RunAsync("codex-cli", ["login", "--device-auth"], session.Cancellation.Token, chunk =>
            {
                lock (sync)
                {
                    buffer += chunk;
                    if (buffer.Length > 16384) buffer = buffer[^16384..];
                    var clean = AnsiSanitizer.Sanitize(buffer);
                    var url = VerificationUrl().Match(clean);
                    var code = UserCode().Match(clean);
                    if (url.Success && code.Success)
                        session.Prompt.TrySetResult(new(code.Value, url.Value, Math.Max(1, (int)(session.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)));
                }
            });
            session.State = result.ExitCode == 0 ? "authorized" : "failed";
            if (result.ExitCode != 0) session.Error = "La connexion Codex a échoué. Relancez l’association.";
        }
        catch (OperationCanceledException) { session.State = "expired"; }
        catch { session.State = "failed"; session.Error = "La connexion Codex n’a pas pu démarrer."; }
        finally { session.Prompt.TrySetException(new InvalidOperationException(session.Error ?? "Aucun code disponible.")); }
    }

    public async Task SaveClaudeTokenAsync(string token, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (runtime.Find("claude-cli") is null) throw new InvalidOperationException("Claude Code n’est pas installé.");
            var path = Path.Combine(runtime.Profile("claude-cli"), "oauth-token.protected");
            var previous = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
            try
            {
                runtime.SaveClaudeToken(token);
                sessions.TryRemove("claude-cli", out _);
                if ((await GetStatusAsync("claude-cli", ct)).State != "authorized")
                    throw new InvalidOperationException("Le jeton n’a pas été reconnu par Claude Code.");
            }
            catch
            {
                if (previous is null) runtime.DeleteClaudeToken();
                else await File.WriteAllBytesAsync(path, previous, CancellationToken.None);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RevokeAuthAsync(string id, CancellationToken ct)
    {
        _ = AiCliRuntime.Executable(id);
        await gate.WaitAsync(ct);
        try
        {
            if (sessions.TryRemove(id, out var session))
            { session.Cancellation.Cancel(); if (session.Run is not null) await session.Run; }
            if (id == "claude-cli") runtime.DeleteClaudeToken();
            if (runtime.Find(id) is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var result = await runtime.RunAsync(id, id == "codex-cli" ? ["logout"] : ["auth", "logout"], timeout.Token);
                if (result.ExitCode != 0) throw new InvalidOperationException("La CLI n’a pas confirmé la déconnexion.");
            }
            // Legacy markers are never accepted as proof of authentication.
            File.Delete(Path.Combine(runtime.Profile(id), "credentials.json"));
            return true;
        }
        finally { gate.Release(); }
    }

    public void Cancel(string id)
    {
        _ = AiCliRuntime.Executable(id);
        if (sessions.TryGetValue(id, out var session) && session.State == "pending")
        {
            session.State = "expired";
            session.Cancellation.Cancel();
        }
    }

    [GeneratedRegex(@"https://(?:auth\.openai\.com|chatgpt\.com)/[a-zA-Z0-9/_-]*device[a-zA-Z0-9/_-]*")]
    private static partial Regex VerificationUrl();
    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4,5}\b")]
    private static partial Regex UserCode();
    public void Dispose() { foreach (var session in sessions.Values) session.Cancellation.Cancel(); }
    private sealed class Session
    {
        public readonly DateTimeOffset ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        public readonly CancellationTokenSource Cancellation = new(TimeSpan.FromMinutes(15));
        public readonly TaskCompletionSource<DeviceAuthPrompt> Prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile string State = "pending";
        public string? Error;
        public Task? Run;
    }
}
