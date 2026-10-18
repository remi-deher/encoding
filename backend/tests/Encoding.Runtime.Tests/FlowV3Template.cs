using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Flow « HEVC + AAC/AC3 + ASS (Crunchyroll) » construit uniquement avec les composants du catalogue (docs/operations/fileflows-v3-flow.md).
/// Le même graphe sert aux tests et au fichier importable docs/operations/flow-templates/hevc-aac-ass.flow.json (comparé octet pour octet).
/// </summary>
internal static class FlowV3Template
{
    public static readonly Guid FlowId = Guid.Parse("3f0e1c52-8b9a-4d0e-9b6e-0a7c5d2f1e01");
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    public static FlowDefinition Build(FlowComponentRegistry registry)
    {
        FlowNodeDefinition Node(string id, string name, string plugin, string component, Dictionary<string, JsonElement>? values = null)
        {
            var reference = registry.FindReference(component, plugin) ?? throw new InvalidOperationException($"Composant {component} introuvable.");
            var manifest = registry.Plugins.Single(item => item.Id == reference.PluginId);
            var descriptor = manifest.Components.Single(item => item.Id == reference.ComponentId);
            return new(id, name, new(reference.PluginId, manifest.Version, descriptor.Id, descriptor.Version, descriptor.ConfigurationSchema.Version), values ?? []);
        }
        var nodes = new List<FlowNodeDefinition>
        {
            Node("entry", "Fichier vidéo", "base", "base-input-video"),
            Node("playback", "Lecture en cours dans Plex", "plex", "plex-check-playback", new() { ["scope"] = J("file"), ["includeNextEpisode"] = J(true), ["nextEpisodeMarginMinutes"] = J(15) }),
            Node("defer-playing", "Reporter : lecture en cours", "base", "base-defer-processing", new() { ["delayMinutes"] = J(10), ["reason"] = J("Lecture en cours dans Plex") }),
            Node("disk", "Verrou de disque", "base", "base-disk-lock", new() { ["disk"] = J("disque-1") }),
            Node("defer-disk", "Reporter : disque occupé", "base", "base-defer-processing", new() { ["delayMinutes"] = J(2), ["reason"] = J("Disque occupé par un autre traitement") }),
            Node("snapshot", "Noms de pistes d'origine", "subtitles", "subtitles-track-names", new() { ["mode"] = J("snapshot") }),
            Node("prepare", "Préparer le plan FFmpeg", "base", "base-prepare-processing", new() { ["tool"] = J("ffmpeg"), ["preserveUnchangedTracks"] = J(true) }),
            Node("video", "Vidéo HEVC", "video", "video-transcode", new() { ["processingMode"] = J("plan"), ["encoderMode"] = J("automatic"), ["videoCodec"] = J("hevc") }),
            Node("audio", "Audio par règles", "audio", "audio-rules", new() { ["processingMode"] = J("plan"), ["unknownLayout"] = J("infer") }),
            Node("execute", "Exécuter le plan (une écriture)", "base", "base-execute-processing"),
            Node("subtitles", "Sous-titres ASS", "subtitles", "subtitles-manage", new() { ["styleProfile"] = J("crunchyroll") }),
            Node("fonts", "Polices", "subtitles", "subtitles-add-fonts", new() { ["fontSet"] = J("Crunchyroll") }),
            Node("names", "Noms de pistes", "subtitles", "subtitles-track-names", new() { ["mode"] = J("apply"), ["singleDefaultAudio"] = J(true) }),
            Node("done", "Terminé", "base", "base-complete-flow", new() { ["result"] = J("success") }),
            Node("fail", "Échec", "base", "base-complete-flow", new() { ["result"] = J("failed"), ["message"] = J("Une étape du flow a échoué ; la source est intacte.") }),
        };
        var edges = new List<FlowConnectionDefinition>();
        void Link(string from, string output, string to) => edges.Add(new($"{from}-{output}", from, output, to));
        Link("entry", "success", "playback");
        Link("playback", "playing", "defer-playing"); Link("playback", "idle", "disk"); Link("playback", "error", "disk");
        Link("disk", "acquired", "snapshot"); Link("disk", "busy", "defer-disk"); Link("disk", "error", "fail");
        Link("snapshot", "success", "prepare"); Link("snapshot", "unchanged", "prepare"); Link("snapshot", "error", "fail");
        Link("prepare", "success", "video"); Link("prepare", "error", "fail");
        Link("video", "success", "audio"); Link("video", "error", "fail");
        Link("audio", "success", "execute"); Link("audio", "unchanged", "execute"); Link("audio", "error", "fail");
        Link("execute", "success", "subtitles"); Link("execute", "skipped", "subtitles"); Link("execute", "error", "fail");
        Link("subtitles", "success", "fonts"); Link("subtitles", "unchanged", "fonts"); Link("subtitles", "error", "fail");
        Link("fonts", "success", "names"); Link("fonts", "unchanged", "names"); Link("fonts", "error", "fail");
        Link("names", "success", "done"); Link("names", "unchanged", "done"); Link("names", "error", "fail");
        return new(1, FlowId, 1, "HEVC + AAC/AC3 + ASS (Crunchyroll)", "video", nodes, edges);
    }
}
