using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed partial class DeviceCodeAuthService(AiCliRuntime runtime, ILogger<DeviceCodeAuthService>? logger = null, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger<DeviceCodeAuthService> log = logger ?? NullLogger<DeviceCodeAuthService>.Instance;
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
            if (status.ExitCode != 0)
            {
                log.Log(status.ExitCode == 1 ? LogLevel.Debug : LogLevel.Warning, "AI CLI status {Provider}: exit={ExitCode}, stdoutChars={OutputChars}, stderrChars={ErrorChars}",
                    id, status.ExitCode, status.Output.Length, status.Error.Length);
                // Exit 1 is the documented unauthenticated status. Other exits indicate a runtime failure.
                return status.ExitCode == 1 ? new(id, "unconfigured") : new(id, "failed",
                    ErrorMessage: $"La CLI a quitté avec le code {status.ExitCode}. Consultez les journaux système de ce nœud.");
            }
            if (id == "claude-cli")
            {
                using var doc = JsonDocument.Parse(status.Output);
                if (!doc.RootElement.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.True)
                    return new(id, "unconfigured");
            }
            return new(id, "authorized");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("AI CLI status {Provider}: failure={Failure}", id, ex.GetType().Name);
            return new(id, "failed", ErrorMessage: ex is OperationCanceledException
                ? "La CLI n’a pas répondu au contrôle de connexion sous 10 secondes."
                : "Le contrôle de connexion CLI a échoué. Consultez les journaux système de ce nœud.");
        }
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
        try { return new(id, await session.Prompt.Task.WaitAsync(TimeSpan.FromSeconds(30), clock, ct)); }
        catch (TimeoutException)
        {
            session.State = "failed";
            session.Error = session.HasOutput
                ? "Codex a produit une sortie, mais aucune URL et aucun code compatibles n’ont été reconnus sous 30 secondes. Consultez les journaux système de ce nœud."
                : "Codex n’a produit aucune sortie sous 30 secondes. Vérifiez son accès au service d’authentification depuis ce nœud.";
            log.LogWarning("AI CLI auth {Attempt}: timeout after {ElapsedMs} ms, outputReceived={HasOutput}, urlRecognized={HasUrl}, codeRecognized={HasCode}",
                session.Id, session.Timer.ElapsedMilliseconds, session.HasOutput, session.HasUrl, session.HasCode);
            session.Cancellation.Cancel();
            throw new InvalidOperationException(session.Error);
        }
    }

    private async Task RunLoginAsync(Session session)
    {
        log.LogInformation("AI CLI auth {Attempt}: starting codex login --device-auth", session.Id);
        try
        {
            var buffer = "";
            var sync = new object();
            var result = await runtime.RunAsync("codex-cli", ["login", "--device-auth"], session.Cancellation.Token, chunk =>
            {
                lock (sync)
                {
                    if (!session.HasOutput && !string.IsNullOrWhiteSpace(chunk))
                    {
                        session.HasOutput = true;
                        log.LogInformation("AI CLI auth {Attempt}: first output after {ElapsedMs} ms", session.Id, session.Timer.ElapsedMilliseconds);
                    }
                    buffer += chunk;
                    if (buffer.Length > 16384) buffer = buffer[^16384..];
                    var clean = AnsiSanitizer.Sanitize(buffer);
                    var url = VerificationUrl().Match(clean);
                    var code = UserCode().Match(clean);
                    session.HasUrl |= url.Success;
                    session.HasCode |= code.Success;
                    if (url.Success && code.Success && session.Prompt.TrySetResult(new(code.Value, url.Value,
                        Math.Max(1, (int)(session.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds))))
                        log.LogInformation("AI CLI auth {Attempt}: prompt recognized after {ElapsedMs} ms", session.Id, session.Timer.ElapsedMilliseconds);
                }
            });
            var promptRecognized = session.Prompt.Task.IsCompletedSuccessfully;
            session.State = result.ExitCode == 0 && promptRecognized ? "authorized" : "failed";
            if (session.State == "failed") session.Error =
                $"Codex a terminé avec le code {result.ExitCode}" + (promptRecognized ? "." : " sans fournir d’URL et de code reconnus.") +
                " Consultez les journaux système de ce nœud.";
            log.Log(session.State == "authorized" ? LogLevel.Information : LogLevel.Warning,
                "AI CLI auth {Attempt}: exit={ExitCode}, elapsedMs={ElapsedMs}, stdoutChars={OutputChars}, stderrChars={ErrorChars}, urlRecognized={HasUrl}, codeRecognized={HasCode}",
                session.Id, result.ExitCode, session.Timer.ElapsedMilliseconds, result.Output.Length, result.Error.Length, session.HasUrl, session.HasCode);
        }
        catch (OperationCanceledException)
        {
            if (session.State == "pending") session.State = "expired";
            log.LogInformation("AI CLI auth {Attempt}: cancelled after {ElapsedMs} ms, state={State}", session.Id, session.Timer.ElapsedMilliseconds, session.State);
        }
        catch (Exception ex)
        {
            session.State = "failed";
            session.Error = "Le processus Codex a échoué. Consultez les journaux système de ce nœud.";
            // No exception message or raw CLI output: either can contain credentials.
            log.LogWarning("AI CLI auth {Attempt}: failure={Failure}, elapsedMs={ElapsedMs}", session.Id, ex.GetType().Name, session.Timer.ElapsedMilliseconds);
        }
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
                var status = await GetStatusAsync("claude-cli", ct);
                if (status.State != "authorized")
                {
                    log.LogWarning("AI CLI Claude: token validation failed, state={State}, runtimeFailure={RuntimeFailure}", status.State, status.ErrorMessage is not null);
                    throw new InvalidOperationException(status.ErrorMessage ?? "Le jeton n’a pas été reconnu par Claude Code.");
                }
                log.LogInformation("AI CLI Claude: token recognized by local status (no remote inference performed)");
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
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly Stopwatch Timer = Stopwatch.StartNew();
        public volatile bool HasOutput;
        public volatile bool HasUrl;
        public volatile bool HasCode;
        public readonly DateTimeOffset ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        public readonly CancellationTokenSource Cancellation = new(TimeSpan.FromMinutes(15));
        public readonly TaskCompletionSource<DeviceAuthPrompt> Prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile string State = "pending";
        public string? Error;
        public Task? Run;
    }
}
