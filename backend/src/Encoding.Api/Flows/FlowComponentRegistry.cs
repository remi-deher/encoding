using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Flows;

/// <summary>Installed descriptors only. Catalog availability does not imply a runnable handler.</summary>
public sealed class FlowComponentRegistry
{
    private readonly Dictionary<FlowComponentReference, FlowComponentDescriptor> components = [];
    public IReadOnlyList<PluginManifest> Plugins { get; }

    public FlowComponentRegistry()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Flows", "Catalog", "base.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Le manifeste Base est vide.");
        if (manifest.ContractVersion != 1 || !manifest.SupportedHostContractVersions.Contains(1)
            || manifest.Id != "base" || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("Le manifeste Base est incompatible.");
        foreach (var component in manifest.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Id) || string.IsNullOrWhiteSpace(component.Version)
                || component.ConfigurationSchema.Version != 1
                || component.Outputs.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != component.Outputs.Count
                || component.ConfigurationSchema.Fields.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != component.ConfigurationSchema.Fields.Count
                || component.ConfigurationSchema.Fields.Any(f => f.Type is not ("text" or "number" or "boolean" or "select")))
                throw new InvalidOperationException($"Descripteur invalide : {component.Id}.");
            var reference = new FlowComponentReference(manifest.Id, manifest.Version, component.Id, component.Version, component.ConfigurationSchema.Version);
            if (!components.TryAdd(reference, component)) throw new InvalidOperationException($"Composant dupliqué : {component.Id}.");
        }
        Plugins = [manifest];
    }

    public FlowComponentDescriptor? Resolve(FlowComponentReference reference) => components.GetValueOrDefault(reference);
}
