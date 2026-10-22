using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Variante cluster du flow V3 : le nœud qui a les fichiers les traite, sauf l'encodage H.265 matériel confié à un nœud capable (docs/operations/fileflows-v3-flow.md).
/// Le même graphe sert aux tests et au fichier importable docs/operations/flow-templates/hevc-aac-ass-cluster.flow.json (comparé octet pour octet).
/// </summary>
internal static class FlowV3ClusterTemplate
{
    public static readonly Guid FlowId = Guid.Parse("3f0e1c52-8b9a-4d0e-9b6e-0a7c5d2f1e02");
    /// <summary>Profil matériel seul : QSV puis VAAPI, aucune cible CPU. À remplacer dans l'éditeur par le profil du même nom créé sur le cluster.</summary>
    public static readonly EncodingProfileSnapshot HardwareProfile = new(Guid.Parse("3f0e1c52-8b9a-4d0e-9b6e-0a7c5d2f1e03"), 1, "Matériel (QSV, VAAPI)", "Encodage matériel Intel, sans repli CPU.",
        [new("hevc_qsv", 25, "medium", 0), new("hevc_vaapi", 23, "", 1)]);
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
            Node("direct", "Accès direct obligatoire", "nodes", "nodes-require-direct-access", new() { ["mode"] = J("strict") }),
            Node("playback", "Lecture en cours dans Plex", "plex", "plex-check-playback", new() { ["scope"] = J("file"), ["includeNextEpisode"] = J(true), ["nextEpisodeMarginMinutes"] = J(15) }),
            Node("defer-playing", "Reporter : lecture en cours", "base", "base-defer-processing", new() { ["delayMinutes"] = J(10), ["reason"] = J("Lecture en cours dans Plex") }),
            Node("snapshot", "Noms de pistes d'origine", "subtitles", "subtitles-track-names", new() { ["mode"] = J("snapshot") }),
            Node("access", "Bibliothèque sur ce nœud (et encodeur)", "nodes", "nodes-library-access", new() { ["requireProfileEncoder"] = J(true) }),
            Node("relay", "Confier à un nœud capable", "nodes", "nodes-handoff", new() { ["mode"] = J("eligible") }),
            Node("need", "Exiger l'encodeur du profil", "nodes", "nodes-require-capability", new() { ["toolId"] = J("ffmpeg"), ["encoderKind"] = J("profile") }),
            Node("prepare", "Préparer le plan FFmpeg", "base", "base-prepare-processing", new() { ["tool"] = J("ffmpeg"), ["preserveUnchangedTracks"] = J(true) }),
            Node("video", "Vidéo HEVC (matériel)", "video", "video-transcode", new() { ["processingMode"] = J("plan"), ["encoderMode"] = J("automatic"), ["videoCodec"] = J("hevc"), ["retryWithCpu"] = J(false), ["colorDepth"] = J("auto"), ["preserveHdr"] = J(true), ["encodingProfile"] = J(HardwareProfile) }),
            Node("audio", "Audio par règles", "audio", "audio-rules", new() { ["processingMode"] = J("plan"), ["unknownLayout"] = J("infer") }),
            Node("execute", "Exécuter le plan (une écriture)", "base", "base-execute-processing"),
            Node("subtitles", "Sous-titres ASS", "subtitles", "subtitles-manage", new() { ["styleProfile"] = J("crunchyroll") }),
            Node("fonts", "Polices", "subtitles", "subtitles-add-fonts", new() { ["fontSet"] = J("Crunchyroll") }),
            Node("names", "Noms de pistes", "subtitles", "subtitles-track-names", new() { ["mode"] = J("apply"), ["singleDefaultAudio"] = J(true) }),
            Node("validate-output", "Contrôles après assemblage", "probe", "probe-validate-output", new()
            {
                ["requiredVideoCodec"] = J("hevc"), ["durationToleranceSeconds"] = J(2), ["preserveTrackCounts"] = J(true),
                ["allowedAudioCodecs"] = J("aac,ac3"), ["forbiddenAudioCodecs"] = J("eac3"),
                ["allowedSubtitleCodecs"] = J("ass,ssa,hdmv_pgs_subtitle,dvd_subtitle,dvb_subtitle"),
                ["maxAudioChannels"] = J(6),
                ["preserveAudioChannels"] = J(true), ["preserveAudioLayout"] = J(true), ["preserveChapterCount"] = J(true),
                ["preserveOriginalAttachments"] = J(true)
            }),
            Node("done", "Terminé", "base", "base-complete-flow", new() { ["result"] = J("success") }),
            Node("fail", "Échec", "base", "base-complete-flow", new() { ["result"] = J("failed"), ["message"] = J("Une étape du flow a échoué ; la source est intacte.") }),
        };
        var edges = new List<FlowConnectionDefinition>();
        void Link(string from, string output, string to) => edges.Add(new($"{from}-{output}", from, output, to));
        Link("entry", "success", "direct"); Link("direct", "success", "playback");
        Link("playback", "playing", "defer-playing"); Link("playback", "idle", "snapshot"); Link("playback", "error", "snapshot");
        Link("snapshot", "success", "access"); Link("snapshot", "unchanged", "access"); Link("snapshot", "error", "fail");
        Link("access", "yes", "prepare"); Link("access", "no", "relay");
        Link("relay", "success", "need"); Link("need", "success", "prepare");
        Link("prepare", "success", "video"); Link("prepare", "error", "fail");
        Link("video", "success", "audio"); Link("video", "error", "fail");
        Link("audio", "success", "execute"); Link("audio", "unchanged", "execute"); Link("audio", "error", "fail");
        Link("execute", "success", "subtitles"); Link("execute", "skipped", "subtitles"); Link("execute", "error", "fail");
        Link("subtitles", "success", "fonts"); Link("subtitles", "unchanged", "fonts"); Link("subtitles", "error", "fail");
        Link("fonts", "success", "names"); Link("fonts", "unchanged", "names"); Link("fonts", "error", "fail");
        Link("names", "success", "validate-output"); Link("names", "unchanged", "validate-output"); Link("names", "error", "fail");
        Link("validate-output", "valid", "done"); Link("validate-output", "invalid", "fail"); Link("validate-output", "error", "fail");
        return new(1, FlowId, 1, "HEVC matériel + AAC/AC3 + ASS (cluster)", "video", nodes, edges);
    }
}
