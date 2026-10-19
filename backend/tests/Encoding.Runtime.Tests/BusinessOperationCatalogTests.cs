using System.Text.Json;
using Encoding.Api.Mcp;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class BusinessOperationCatalogTests
{
    [Fact]
    public void ResumeExecutionIsAnAdminConfirmedMutation()
    {
        var operation = BusinessOperationCatalog.FindTool("resume_execution");

        Assert.NotNull(operation);
        Assert.Equal("executions-and-logs", operation.Domain);
        Assert.False(operation.ReadOnly);
        Assert.True(operation.RequiresAdministrator);
        Assert.True(operation.RequiresConfirmation);
        Assert.False(operation.AvailableToAssistant);
        Assert.True(BusinessOperationCatalog.IsAssistantAction("resume_execution"));

        using var valid = JsonDocument.Parse("{\"id\":\"b588f5bf-0bbd-45b9-a5db-7c73dfc064c7\"}");
        using var invalid = JsonDocument.Parse("{\"id\":\"b588f5bf-0bbd-45b9-a5db-7c73dfc064c7\",\"force\":true}");
        Assert.True(BusinessOperationCatalog.ValidateArguments("resume_execution", valid.RootElement));
        Assert.False(BusinessOperationCatalog.ValidateArguments("resume_execution", invalid.RootElement));
    }
}
