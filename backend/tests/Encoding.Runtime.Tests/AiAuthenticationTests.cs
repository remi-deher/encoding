using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiAuthenticationTests
{
    [Fact]
    public async Task ProductionRuntimeFailsClosedWithoutLandlockOrDeniesFilesOutsideProfile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new AiCliTestProfile("printf 'not-isolated'");
        var production = new AiCliRuntime(fixture.Configuration, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        if (!Encoding.Core.Runtime.ProcessSandbox.IsAvailable)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => production.RunAsync("codex-cli", ["exec"], CancellationToken.None));
            return;
        }
        var sentinel = Path.Combine(fixture.DirectoryPath, "sentinel");
        File.WriteAllText(sentinel, "private-sentinel");
        var script = fixture.Configuration["AiTools:Executables:codex-cli"]!;
        File.WriteAllText(script, "#!/bin/sh\nif cat '" + sentinel + "' 2>/dev/null; then exit 1; else printf 'isolated'; fi");
        var result = await production.RunAsync("codex-cli", ["exec"], CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("isolated", result.Output);
    }

    [Fact]
    public async Task CodexUsesProviderCodeAndRealProfileStatus()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            if [ "$1 $2" = "login --device-auth" ]; then
              printf '\033[32mOpen https://auth.openai.com/codex/device\033[0m\nABCD-23456\n'
              sleep 0.2
              touch "$CODEX_HOME/auth.valid"
              exit 0
            fi
            if [ "$1 $2" = "login status" ]; then test -f "$CODEX_HOME/auth.valid"; exit $?; fi
            if [ "$1" = "logout" ]; then rm -f "$CODEX_HOME/auth.valid"; exit 0; fi
            exit 1
            """);
        using var service = new DeviceCodeAuthService(fixture.Runtime);
        var prompt = await service.StartDeviceAuthAsync("codex-cli", CancellationToken.None);
        Assert.Equal("ABCD-23456", prompt.Prompt.UserCode);
        Assert.Equal("https://auth.openai.com/codex/device", prompt.Prompt.VerificationUri);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await service.GetStatusAsync("codex-cli", deadline.Token)).State == "pending") await Task.Delay(20, deadline.Token);
        Assert.Equal("authorized", (await service.GetStatusAsync("codex-cli")).State);
        Assert.Equal("authorized", (await new DeviceCodeAuthService(fixture.Runtime).GetStatusAsync("codex-cli")).State);
        await service.RevokeAuthAsync("codex-cli", CancellationToken.None);
        Assert.Equal("unconfigured", (await service.GetStatusAsync("codex-cli")).State);
    }

    [Fact]
    public async Task ClaudeTokenIsProtectedAndNeverReturnedFromStatus()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            if [ "$1 $2" = "auth status" ]; then
              if [ "$CLAUDE_CODE_OAUTH_TOKEN" = "test-private-token" ]; then printf '{"loggedIn":true}'; exit 0; fi
              printf '{"loggedIn":false}'; exit 1
            fi
            exit 0
            """);
        using var service = new DeviceCodeAuthService(fixture.Runtime);
        await service.SaveClaudeTokenAsync("test-private-token", CancellationToken.None);
        var saved = File.ReadAllText(Path.Combine(fixture.Runtime.Profile("claude-cli"), "oauth-token.protected"));
        Assert.DoesNotContain("test-private-token", saved);
        var status = await service.GetStatusAsync("claude-cli");
        Assert.Equal("authorized", status.State);
        Assert.Null(status.AccountName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveClaudeTokenAsync("bad-token", CancellationToken.None));
        Assert.Equal("authorized", (await service.GetStatusAsync("claude-cli")).State);
        await service.RevokeAuthAsync("claude-cli", CancellationToken.None);
        Assert.False(fixture.Runtime.HasClaudeToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveClaudeTokenAsync("bad-token", CancellationToken.None));
        Assert.False(fixture.Runtime.HasClaudeToken);
    }

    [Fact]
    public async Task InferenceUsesSameIsolatedProfileAndOfficialArguments()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            if [ "$1" != "exec" ]; then exit 1; fi
            if [ -z "$CODEX_HOME" ] || [ -n "$OPENAI_API_KEY" ]; then exit 2; fi
            cat >/dev/null
            printf 'Real CLI test result'
            """);
        var result = await new CliAiTextClient("codex-cli", fixture.Runtime).GenerateTextAsync(new AiPrompt("Instructions", "Request"));
        Assert.True(result.Success);
        Assert.Equal("Real CLI test result", result.Content);
    }
}
