using System.Text.Json;

namespace Encoding.Contracts.Plugins;

/// <summary>Data-only catalog exchanged with an isolated plugin process.</summary>
public sealed record PluginManifest(
    int ContractVersion,
    string Id,
    string Version,
    string Name,
    IReadOnlyList<int> SupportedHostContractVersions,
    IReadOnlyList<FlowComponentDescriptor> Components,
    FlowConfigurationSchema? ConfigurationSchema = null);

public sealed record FlowComponentDescriptor(
    string Id,
    string Version,
    string Name,
    string Description,
    string Kind,
    string MediaType,
    IReadOnlyList<string> SupportedFlowMediaTypes,
    IReadOnlyList<FlowOutputDescriptor> Outputs,
    FlowConfigurationSchema ConfigurationSchema,
    IReadOnlyList<FlowToolRequirement> RequiredTools,
    bool RequiresMedia,
    bool RequiresWriteAccess);

public sealed record FlowOutputDescriptor(string Id, string Label, string Description);

public sealed record FlowToolRequirement(
    string ToolId,
    string? MinimumVersion,
    IReadOnlyList<string> RequiredCapabilities);

/// <summary>Uses the editor's text, number, boolean and select field vocabulary.</summary>
public sealed record FlowConfigurationSchema(
    int Version,
    IReadOnlyList<FlowConfigurationField> Fields);

public sealed record FlowConfigurationField(
    string Id,
    string Type,
    string Label,
    string? Description = null,
    bool Required = false,
    bool Inline = false,
    JsonElement? DefaultValue = null,
    string? Placeholder = null,
    bool Multiline = false,
    decimal? Min = null,
    decimal? Max = null,
    decimal? Step = null,
    IReadOnlyList<decimal>? Suggestions = null,
    IReadOnlyList<FlowSelectOption>? Options = null);

public sealed record FlowSelectOption(
    string Value,
    string Label,
    IReadOnlyList<string>? SupportedFlowMediaTypes = null);
