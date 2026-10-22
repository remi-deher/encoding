using System.Text.Json;
using Encoding.Api.Mcp;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class McpInitializeSerializationTests
{
    [Fact]
    public void InitializeCapabilitiesOmitUnsupportedPromptsAndResources()
    {
        var result = new McpInitializeResult(
            "2024-11-05",
            new McpCapabilities(Tools: new McpToolsCapability(ListChanged: false)),
            new McpServerInfo("encoding-mcp", "1.0.0"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var capabilities = json.RootElement.GetProperty("capabilities");

        Assert.True(capabilities.TryGetProperty("tools", out _));
        Assert.False(capabilities.TryGetProperty("prompts", out _));
        Assert.False(capabilities.TryGetProperty("resources", out _));
    }
}
