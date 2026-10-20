using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;
using Encoding.Contracts.Flows.Conditions;

namespace Encoding.Api.Flows;

/// <summary>Installed descriptors only. Catalog availability does not imply a runnable handler.</summary>
public sealed class FlowComponentRegistry
{
    private readonly Dictionary<FlowComponentReference, FlowComponentDescriptor> components = [];
    private readonly Dictionary<FlowComponentReference,string> runtimeVersions=[];
    private readonly FlowComponentDescriptor legacyCondition;
    public IReadOnlyList<PluginManifest> Plugins { get; }

    public FlowComponentRegistry(IConfiguration configuration)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Flows", "Catalog", "base.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Le manifeste Base est vide.");
        if (manifest.ContractVersion != 1 || !manifest.SupportedHostContractVersions.Contains(1)
            || manifest.Id != "base" || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("Le manifeste Base est incompatible.");
        var legacyPath = Path.Combine(AppContext.BaseDirectory, "Flows", "Legacy", "base-condition.json");
        legacyCondition = JsonSerializer.Deserialize<FlowComponentDescriptor>(File.ReadAllText(legacyPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Le composant Base historique est vide.");
        var manifests = new List<PluginManifest> { manifest };
        var catalogDirectory = Path.Combine(AppContext.BaseDirectory, "Flows", "Catalog");
        foreach (var file in Directory.EnumerateFiles(catalogDirectory, "*.json")
                     .Where(file => !Path.GetFileName(file).Equals("base.json", StringComparison.OrdinalIgnoreCase))
                     .Order())
        {
            var extra = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Manifeste plugin intégré invalide.");
            if (extra.Id == "base" || extra.ContractVersion != 1 || !extra.SupportedHostContractVersions.Contains(1))
                throw new InvalidOperationException("Manifeste plugin intégré incompatible.");
            manifests.Add(extra);
        }
        var additional = configuration["FlowPlugins:ManifestDirectory"];
        if (additional is not null && Directory.Exists(additional))
        {
            foreach (var file in Directory.EnumerateFiles(additional, "*.json").Order())
            {
                var extra = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Manifeste plugin invalide.");
                if (extra.Id == "base" || extra.ContractVersion != 1 || !extra.SupportedHostContractVersions.Contains(1))
                    throw new InvalidOperationException("Manifeste plugin incompatible.");
                manifests.Add(extra);
            }
        }
        manifests=manifests.Select(installed=>installed with
        {
            Components=installed.Components.Select(component=>component with
            {
                ConfigurationSchema=component.ConfigurationSchema with
                {
                    Fields=component.ConfigurationSchema.Fields.Select(field=>field.Type=="condition-tree"?field with
                    {
                        ConditionFields=ConditionTree.Fields,
                        ConditionPresets=ConditionTree.Presets,
                        DefaultValue=field.DefaultValue??ConditionTree.Serialize(new ConditionGroup("all",[]))
                    }:field.Type=="encoding-profile"?field with
                    {
                        DefaultValue=field.DefaultValue??JsonSerializer.SerializeToElement(BuiltInEncodingProfiles.Balanced,new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    }:field).ToArray()
                }
            }).ToArray()
        }).ToList();
        foreach (var installed in manifests)
        {
            if (installed.ConfigurationSchema is { } pluginSchema
                && (pluginSchema.Version != 1
                    || pluginSchema.Fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != pluginSchema.Fields.Count
                    || pluginSchema.Fields.Any(field => field.Type is not ("text" or "secret" or "number" or "boolean" or "select"))))
                throw new InvalidOperationException($"Schéma de configuration invalide : {installed.Id}.");
            if (installed.PublicationGuard is { } guard)
            {
                var guardComponent = installed.Components.SingleOrDefault(item => item.Id == guard.ComponentId);
                var pluginFields = installed.ConfigurationSchema?.Fields.ToDictionary(field => field.Id, StringComparer.Ordinal) ?? [];
                var componentFields = guardComponent?.ConfigurationSchema.Fields.Select(field => field.Id).ToHashSet(StringComparer.Ordinal) ?? [];
                var outputs = guardComponent?.Outputs.Select(output => output.Id).ToHashSet(StringComparer.Ordinal) ?? [];
                if (guardComponent is null
                    || !pluginFields.TryGetValue(guard.EnabledField, out var enabled) || enabled.Type != "boolean"
                    || !pluginFields.TryGetValue(guard.MaxWaitField, out var maxWait) || maxWait.Type != "number"
                    || guard.WaitOutputs.Count == 0 || guard.AllowOutputs.Count == 0
                    || guard.WaitOutputs.Concat(guard.AllowOutputs).Any(id => !outputs.Contains(id))
                    || guard.WaitOutputs.Intersect(guard.AllowOutputs).Any()
                    || guard.FixedConfiguration.Keys.Concat(guard.ConfigurationFields.Keys).Any(id => !componentFields.Contains(id))
                    || guard.FixedConfiguration.Keys.Intersect(guard.ConfigurationFields.Keys).Any()
                    || guard.ConfigurationFields.Values.Any(id => !pluginFields.ContainsKey(id)))
                    throw new InvalidOperationException($"Garde de publication invalide : {installed.Id}.");
            }
            foreach (var component in installed.Components)
            {
                if (string.IsNullOrWhiteSpace(component.Id) || string.IsNullOrWhiteSpace(component.Version)
                    || component.ConfigurationSchema.Version != 1
                    || component.Outputs.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != component.Outputs.Count
                    || component.ConfigurationSchema.Fields.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != component.ConfigurationSchema.Fields.Count
                    || component.ConfigurationSchema.Fields.Any(f => f.Type is not ("text" or "number" or "boolean" or "select" or "condition-tree" or "script-reference" or "encoding-profile" or "video-encoder")))
                    throw new InvalidOperationException($"Descripteur invalide : {component.Id}.");
                var reference = new FlowComponentReference(
                    installed.Id,
                    installed.Version,
                    component.Id,
                    component.Version,
                    component.ConfigurationSchema.Version);
                if (!components.TryAdd(reference, component)) throw new InvalidOperationException($"Composant dupliqué : {component.Id}.");
                runtimeVersions.Add(reference,installed.Version);
                foreach(var version in component.CompatiblePluginVersions??[])
                {
                    var compatible=reference with { PluginVersion=version };
                    if(string.IsNullOrWhiteSpace(version) || !components.TryAdd(compatible,component))
                        throw new InvalidOperationException($"Référence compatible dupliquée ou invalide : {component.Id}.");
                    runtimeVersions.Add(compatible,installed.Version);
                }
            }
        }
        var legacyReference = new FlowComponentReference(manifest.Id, manifest.Version, legacyCondition.Id,
            legacyCondition.Version, legacyCondition.ConfigurationSchema.Version);
        components.Add(legacyReference, legacyCondition);
        runtimeVersions.Add(legacyReference, manifest.Version);
        foreach (var version in legacyCondition.CompatiblePluginVersions ?? [])
        {
            var compatible = legacyReference with { PluginVersion = version };
            components.Add(compatible, legacyCondition);
            runtimeVersions.Add(compatible, manifest.Version);
        }
        Plugins = manifests;
    }

    private static readonly Dictionary<string, (string PluginId, string ComponentId)> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // MKV plugin
        ["base-remux-matroska"] = ("mkv", "mkv-remux-matroska"),
        ["base-remove-tracks"] = ("mkv", "mkv-remove-tracks"),
        ["base-extract-track"] = ("mkv", "mkv-extract-track"),
        ["base-reorder-tracks"] = ("mkv", "mkv-reorder-tracks"),
        ["base-edit-track-properties"] = ("mkv", "mkv-edit-track-properties"),
        ["base-edit-matroska-metadata"] = ("mkv", "mkv-edit-matroska-metadata"),
        ["base-extract-attachments"] = ("mkv", "mkv-extract-attachments"),
        ["base-remove-attachments"] = ("mkv", "mkv-remove-attachments"),
        ["base-extract-chapters"] = ("mkv", "mkv-extract-chapters"),
        ["base-replace-chapters"] = ("mkv", "mkv-replace-chapters"),
        // Images plugin
        ["base-convert-image"] = ("images", "image-convert"),
        ["base-resize-image"] = ("images", "image-resize"),
        ["base-crop-image"] = ("images", "image-crop"),
        // Probe plugin
        ["base-probe-media"] = ("probe", "probe-media"),
        ["base-probe-tracks"] = ("probe", "probe-tracks"),
        ["base-probe-video"] = ("probe", "probe-video"),
        ["base-probe-audio"] = ("probe", "probe-audio"),
        ["base-inspect-media"] = ("probe", "probe-inspect-media"),
        ["base-inspect-hdr"] = ("probe", "probe-inspect-hdr"),
        ["base-inspect-track"] = ("probe", "probe-inspect-track"),
        ["base-export-media-report"] = ("probe", "probe-export-report"),
        // Video plugin
        ["base-remux-media"] = ("video", "video-remux-media"),
        ["base-transcode-video"] = ("video", "video-transcode"),
        ["base-resize-video"] = ("video", "video-resize"),
        ["base-auto-crop"] = ("video", "video-auto-crop"),
        ["base-deinterlace-video"] = ("video", "video-deinterlace"),
        ["base-video-thumbnail"] = ("video", "video-thumbnail"),
        ["base-verify-decoding"] = ("video", "video-verify-decoding"),
        ["base-file-size-guard"] = ("video", "video-file-size-guard"),
        // Audio plugin
        ["base-transcode-audio"] = ("audio", "audio-transcode"),
        ["base-normalize-audio"] = ("audio", "audio-normalize"),
        ["base-extract-audio"] = ("audio", "audio-extract"),
        ["base-audio-rules"] = ("audio", "audio-rules"),
        ["base-add-audio-track"] = ("audio", "audio-add-track"),
        ["base-filter-languages"] = ("audio", "audio-filter-languages"),
        ["base-remove-commentary"] = ("audio", "audio-remove-commentary"),
        // Subtitles plugin
        ["base-extract-subtitles"] = ("subtitles", "subtitles-extract"),
        ["base-subtitle-manage"] = ("subtitles", "subtitles-manage"),
        ["base-add-fonts"] = ("subtitles", "subtitles-add-fonts"),
        ["base-add-subtitle-track"] = ("subtitles", "subtitles-add-track"),
        ["base-track-names"] = ("subtitles", "subtitles-track-names"),
    };

    public static bool TryGetLegacyAlias(string componentId, out (string PluginId, string ComponentId) target) =>
        LegacyAliases.TryGetValue(componentId, out target);

    public FlowComponentDescriptor? Resolve(FlowComponentReference reference)
    {
        if (components.TryGetValue(reference, out var descriptor))
            return descriptor;

        if (reference.PluginId == "base" && LegacyAliases.TryGetValue(reference.ComponentId, out var target))
        {
            var targetRef = FindReference(target.ComponentId, target.PluginId);
            if (targetRef is not null && components.TryGetValue(targetRef, out var aliased))
                return aliased;
        }

        return null;
    }

    public string? RuntimePluginVersion(FlowComponentReference reference)
    {
        if (runtimeVersions.TryGetValue(reference, out var v))
            return v;

        if (reference.PluginId == "base" && LegacyAliases.TryGetValue(reference.ComponentId, out var target))
        {
            var targetRef = FindReference(target.ComponentId, target.PluginId);
            if (targetRef is not null && runtimeVersions.TryGetValue(targetRef, out var aliasedVersion))
                return aliasedVersion;
        }

        return null;
    }

    public FlowComponentReference? FindReference(string componentId, string? pluginId = null)
    {
        foreach (var manifest in Plugins)
        {
            if (pluginId is not null && !string.Equals(manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                continue;
            var comp = manifest.Components.FirstOrDefault(c => string.Equals(c.Id, componentId, StringComparison.OrdinalIgnoreCase));
            if (comp is not null)
            {
                return new FlowComponentReference(
                    manifest.Id,
                    manifest.Version,
                    comp.Id,
                    comp.Version,
                    comp.ConfigurationSchema.Version);
            }
        }

        if (LegacyAliases.TryGetValue(componentId, out var target))
        {
            return FindReference(target.ComponentId, target.PluginId);
        }

        if (string.Equals(componentId, "base-condition", StringComparison.OrdinalIgnoreCase)
            && (pluginId is null || string.Equals(pluginId, "base", StringComparison.OrdinalIgnoreCase)))
        {
            var manifest = Plugins.Single(plugin => plugin.Id == "base");
            return new FlowComponentReference(manifest.Id, manifest.Version, legacyCondition.Id,
                legacyCondition.Version, legacyCondition.ConfigurationSchema.Version);
        }

        return null;
    }
}

