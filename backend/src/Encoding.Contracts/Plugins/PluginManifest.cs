using System.Text.Json;
using Encoding.Contracts.Flows.Conditions;

namespace Encoding.Contracts.Plugins;

/// <summary>Data-only catalog exchanged with an isolated plugin process.</summary>
public sealed record PluginManifest(
    int ContractVersion,
    string Id,
    string Version,
    string Name,
    IReadOnlyList<int> SupportedHostContractVersions,
    IReadOnlyList<FlowComponentDescriptor> Components,
    FlowConfigurationSchema? ConfigurationSchema = null,
    PluginPublicationGuard? PublicationGuard = null);

/// <summary>
/// Point d'extension (architecture 0059) : le plugin fournit une garde consultée par le nœud d'accès avant de remplacer un
/// fichier source. Le composant <see cref="ComponentId"/> est exécuté avec la configuration fixe <see cref="FixedConfiguration"/>
/// complétée par les champs du plugin <see cref="ConfigurationFields"/> (champ du composant -> champ du plugin) ; une sortie de
/// <see cref="WaitOutputs"/> retarde le remplacement, une sortie de <see cref="AllowOutputs"/> l'autorise, toute autre est un refus.
/// </summary>
public sealed record PluginPublicationGuard(
    string ComponentId,
    string EnabledField,
    string MaxWaitField,
    IReadOnlyDictionary<string, JsonElement> FixedConfiguration,
    IReadOnlyDictionary<string, string> ConfigurationFields,
    IReadOnlyList<string> WaitOutputs,
    IReadOnlyList<string> AllowOutputs);

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
    bool RequiresWriteAccess,
    IReadOnlyList<string>? CompatiblePluginVersions = null);

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
    IReadOnlyList<FlowSelectOption>? Options = null,
    IReadOnlyList<ConditionFieldDefinition>? ConditionFields = null,
    IReadOnlyList<ConditionPreset>? ConditionPresets = null,
    string? VisibleWhenFieldId = null,
    string? VisibleWhenValue = null,
    bool VisibleWhenEmpty = false,
    string? Importance = null,
    string? Section = null,
    string? Editor = null);

public sealed record FlowSelectOption(
    string Value,
    string Label,
    IReadOnlyList<string>? SupportedFlowMediaTypes = null);
