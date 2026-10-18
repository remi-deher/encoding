using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using Encoding.NodeAgent.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiToolTests
{
    [Fact]
    public void HardwareCapabilities_Protects_Accelerators()
    {
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["nvenc"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["vaapi"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["qsv"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["cuda"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(["ai-text-generation"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(["subtitles-translation"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(null));
    }

    [Fact]
    public void ScriptTools_Includes_AiCliTools()
    {
        Assert.Contains("codex", ScriptTools.AllowedTools);
        Assert.Contains("claude", ScriptTools.AllowedTools);
    }

    [Fact]
    public void DeviceCodeAuthService_Lifecycle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "enc_ai_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiTools:CredentialsDirectory"] = tempDir
                })
                .Build();

            var service = new DeviceCodeAuthService(config);

            // 1. Initial status: unconfigured
            var status = service.GetStatus("codex-cli");
            Assert.Equal("unconfigured", status.State);

            // 2. Start device auth
            var start = service.StartDeviceAuth("codex-cli");
            Assert.Equal("codex-cli", start.ToolId);
            Assert.False(string.IsNullOrWhiteSpace(start.Prompt.UserCode));
            Assert.Contains("chatgpt.com", start.Prompt.VerificationUri);

            // 3. Status is now pending
            var pendingStatus = service.GetStatus("codex-cli");
            Assert.Equal("pending", pendingStatus.State);

            // 4. Complete authorization
            var completed = service.CompleteAuthorization("codex-cli", "mon-compte-chatgpt");
            Assert.True(completed);

            // 5. Status is now authorized
            var authStatus = service.GetStatus("codex-cli");
            Assert.Equal("authorized", authStatus.State);
            Assert.Equal("mon-compte-chatgpt", authStatus.AccountName);

            // 6. List tools shows authorized
            var tools = service.ListTools();
            var codex = tools.Single(t => t.Id == "codex-cli");
            Assert.Equal("authorized", codex.AuthStatus.State);

            // 7. Revoke
            var revoked = service.RevokeAuth("codex-cli");
            Assert.True(revoked);
            var afterRevoke = service.GetStatus("codex-cli");
            Assert.Equal("unconfigured", afterRevoke.State);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
