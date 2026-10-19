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
        return await CheckCliStatusAsync(id, ct);
    }

    private async Task<DeviceAuthStatus> CheckCliStatusAsync(string id, CancellationToken ct)
    {
        if (runtime.Find(id) is null) return new(id, "unconfigured", ErrorMessage: "Outil non installé.");
        if (!runtime.CanRun) return new(id, "failed", ErrorMessage: "Landlock est requis pour exécuter les outils IA sur ce nœud Linux.");
        if (id == "claude-cli" && !runtime.HasClaudeToken) return new(id, "unconfigured");
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
        _ = AiCliRuntime.Executable(id);
        if (runtime.Find(id) is null) throw new InvalidOperationException("La CLI n’est pas installée sur ce nœud.");
        if (!runtime.CanRun) throw new InvalidOperationException("Landlock est requis pour exécuter les outils IA sur ce nœud Linux.");
        Session session;
        await gate.WaitAsync(ct);
        try
        {
            if (!sessions.TryGetValue(id, out session!) || session.State != "pending" || session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                if (session is not null) session.Cancellation.Cancel();
                session = new(id);
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
                ? "La CLI a produit une sortie, mais aucune URL de connexion reconnue n’a été détectée sous 30 secondes. Consultez les journaux système de ce nœud."
                : "La CLI n’a produit aucune sortie sous 30 secondes. Vérifiez son accès au service d’authentification depuis ce nœud.";
            log.LogWarning("AI CLI auth {Attempt}: timeout after {ElapsedMs} ms, outputReceived={HasOutput}, urlRecognized={HasUrl}, codeRecognized={HasCode}",
                session.Id, session.Timer.ElapsedMilliseconds, session.HasOutput, session.HasUrl, session.HasCode);
            session.Cancellation.Cancel();
            throw new InvalidOperationException(session.Error);
        }
    }

    private async Task RunLoginAsync(Session session)
    {
        IReadOnlyList<string> args = session.ToolId == "codex-cli" ? ["login", "--device-auth"] : ["auth", "login"];
        log.LogInformation("AI CLI auth {Attempt}: starting {Provider} login", session.Id, session.ToolId);
        AiCliProcess? process = null;
        try
        {
            var buffer = "";
            var sync = new object();
            lock (sync)
            {
                process = runtime.StartInteractive(session.ToolId, args, chunk =>
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
                        var url = FindVerificationUrl(session.ToolId, clean);
                        var code = UserCode().Match(clean);
                        session.HasUrl |= url is not null;
                        session.HasCode |= session.ToolId == "codex-cli" && code.Success;
                        if (url is not null && (session.ToolId == "claude-cli" || code.Success) && session.Prompt.TrySetResult(new(
                            session.ToolId == "codex-cli" ? code.Value : null,
                            url,
                            Math.Max(1, (int)(session.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
                            5,
                            session.ToolId == "claude-cli")))
                            log.LogInformation("AI CLI auth {Attempt}: prompt recognized after {ElapsedMs} ms", session.Id, session.Timer.ElapsedMilliseconds);
                    }
                });
                session.Process = process;
            }
            if (session.ToolId == "codex-cli") process.CloseInput();
            var result = await process.WaitAsync(session.Cancellation.Token);
            session.Process = null;
            var promptRecognized = session.Prompt.Task.IsCompletedSuccessfully;
            var verified = result.ExitCode == 0 && promptRecognized
                ? await CheckCliStatusAsync(session.ToolId, session.Cancellation.Token)
                : null;
            session.State = verified?.State == "authorized" ? "authorized" : "failed";
            if (session.State == "failed") session.Error = result.ExitCode != 0
                ? $"La CLI a terminé avec le code {result.ExitCode}. Consultez les journaux système de ce nœud."
                : promptRecognized
                    ? verified?.ErrorMessage ?? "La CLI n’a pas confirmé la connexion. Relancez l’association."
                    : "La CLI a terminé sans fournir d’invitation de connexion reconnue. Consultez les journaux système de ce nœud.";
            log.Log(session.State == "authorized" ? LogLevel.Information : LogLevel.Warning,
                "AI CLI auth {Attempt}: provider={Provider}, exit={ExitCode}, elapsedMs={ElapsedMs}, stdoutChars={OutputChars}, stderrChars={ErrorChars}, urlRecognized={HasUrl}, codeRecognized={HasCode}, inputSubmitted={InputSubmitted}",
                session.Id, session.ToolId, result.ExitCode, session.Timer.ElapsedMilliseconds, result.Output.Length, result.Error.Length, session.HasUrl, session.HasCode, session.InputSubmitted);
        }
        catch (OperationCanceledException)
        {
            if (session.State == "pending") session.State = "expired";
            log.LogInformation("AI CLI auth {Attempt}: cancelled after {ElapsedMs} ms, state={State}", session.Id, session.Timer.ElapsedMilliseconds, session.State);
        }
        catch (Exception ex)
        {
            session.State = "failed";
            session.Error = $"Le processus {session.ToolId} a échoué. Consultez les journaux système de ce nœud.";
            // No exception message or raw CLI output: either can contain credentials.
            log.LogWarning("AI CLI auth {Attempt}: failure={Failure}, elapsedMs={ElapsedMs}", session.Id, ex.GetType().Name, session.Timer.ElapsedMilliseconds);
        }
        finally
        {
            session.Process = null;
            process?.Dispose();
            session.Prompt.TrySetException(new InvalidOperationException(session.Error ?? "Aucun lien de connexion disponible."));
        }
    }

    public async Task SubmitAuthInputAsync(string id, string input, CancellationToken ct = default)
    {
        if (id != "claude-cli") throw new ArgumentException("Cette CLI n’attend pas de code de retour.");
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("Le code de connexion Claude est invalide.");
        if (!sessions.TryGetValue(id, out var session)) throw new InvalidOperationException("Aucune connexion Claude n’est en attente.");
        await session.InputGate.WaitAsync(ct);
        try
        {
            if (session.State != "pending" || !session.Prompt.Task.IsCompletedSuccessfully || !session.Prompt.Task.Result.RequiresInput)
                throw new InvalidOperationException("La connexion Claude n’attend plus de code.");
            if (session.InputSubmitted) throw new InvalidOperationException("Un code a déjà été transmis à Claude.");
            if (session.Process is null) throw new InvalidOperationException("La session Claude n’accepte plus de code.");
            session.InputSubmitted = true;
            try { await session.Process.WriteInputLineAsync(value, ct); }
            catch
            {
                session.InputSubmitted = false;
                throw new InvalidOperationException("Impossible de transmettre le code à Claude Code.");
            }
        }
        finally { session.InputGate.Release(); }
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

    private static string? FindVerificationUrl(string id, string text)
    {
        var match = (id == "codex-cli" ? CodexVerificationUrl() : ClaudeVerificationUrl()).Match(text);
        if (!match.Success) return null;
        var candidate = match.Value.TrimEnd('.', ',', ';', '!', ')', ']');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var allowed = id == "codex-cli"
            ? uri.Host is "auth.openai.com" or "chatgpt.com"
            : uri.Host is "claude.com" or "console.anthropic.com";
        return allowed ? candidate : null;
    }
    [GeneratedRegex(@"https://(?:auth\.openai\.com|chatgpt\.com)/[a-zA-Z0-9/_-]*device[a-zA-Z0-9/_-]*")]
    private static partial Regex CodexVerificationUrl();
    [GeneratedRegex(@"https://(?:claude\.com|console\.anthropic\.com)/[^\s<>""']+")]
    private static partial Regex ClaudeVerificationUrl();
    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4,5}\b")]
    private static partial Regex UserCode();
    public void Dispose() { foreach (var session in sessions.Values) session.Cancellation.Cancel(); }
    private sealed class Session
    {
        public Session(string toolId) => ToolId = toolId;
        public readonly string ToolId;
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly Stopwatch Timer = Stopwatch.StartNew();
        public volatile bool HasOutput;
        public volatile bool HasUrl;
        public volatile bool HasCode;
        public readonly DateTimeOffset ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        public readonly CancellationTokenSource Cancellation = new(TimeSpan.FromMinutes(15));
        public readonly TaskCompletionSource<DeviceAuthPrompt> Prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly SemaphoreSlim InputGate = new(1);
        public volatile string State = "pending";
        public volatile bool InputSubmitted;
        public AiCliProcess? Process;
        public string? Error;
        public Task? Run;
    }
}
