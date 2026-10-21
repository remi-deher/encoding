using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Api.Flows;
using Encoding.Api.Plugins;
using Encoding.Contracts.Plugins;
using Microsoft.Extensions.Configuration;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

internal static class PluginTests
{
    public static void Run()
    {
        PathMappings();
        WebhookPayloads();
        CatalogAcceptsManifests();
        InvalidManifestsAreRejected();
    }

    private static void PathMappings()
    {
        // Les vecteurs sont partagés avec les tests Python : les deux implémentations doivent concorder.
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "plugins", "tests", "path-mapping-vectors.json")));
        var root = vectors.RootElement;
        var libraries = root.GetProperty("libraries").EnumerateObject().ToDictionary(item => item.Name, item => Guid.Parse(item.Value.GetString()!));
        string Fill(string text) => libraries.Aggregate(text, (current, item) => current.Replace("{" + item.Key + "}", item.Value.ToString()));
        var mappings = LibraryPathMapping.Parse(Fill(root.GetProperty("mappings").GetString()!));
        Check(mappings.ToDictionary(item => item.LibraryId, item => item.RemoteRoot).SequenceEqual(
            root.GetProperty("roots").EnumerateObject().ToDictionary(item => libraries[item.Name], item => item.Value.GetString()!)), "Roots are normalized.");
        foreach (var vector in root.GetProperty("toRemote").EnumerateArray())
            Check(LibraryPathMapping.ToRemote(mappings, libraries[vector.GetProperty("library").GetString()!], vector.GetProperty("relative").GetString()!) == vector.GetProperty("remote").GetString(),
                "A library file maps to the service path.");
        Check(LibraryPathMapping.ToRemote(mappings, Guid.NewGuid(), "a.mkv") is null, "An unmapped library has no remote path.");
        foreach (var invalid in root.GetProperty("invalid").EnumerateArray())
        {
            var rejected = false;
            try { LibraryPathMapping.Parse(Fill(invalid.GetString()!)); } catch (FormatException) { rejected = true; }
            Check(rejected, "Invalid mapping rejected: " + invalid.GetString());
        }

        var tv = libraries["tv"];
        var movies = libraries["movies"];
        Check(LibraryPathMapping.ToLibrary(mappings, "/tv/Show/S01/e1.mkv") == (tv, "Show/S01/e1.mkv"), "A remote file maps to its library and relative path.");
        Check(LibraryPathMapping.ToLibrary(mappings, "/mnt/movies/4k/Film/film.mkv") == (movies, "Film/film.mkv"), "Windows separators are accepted.");
        Check(LibraryPathMapping.ToLibrary(mappings, "/tvshows/e1.mkv") is null, "A sibling directory is not inside the root.");
        Check(LibraryPathMapping.ToLibrary(mappings, "/tv/../etc/passwd") is null, "Traversal is rejected.");
        Check(LibraryPathMapping.ToLibrary(mappings, "/tv") is null, "The root itself is not a file.");
        var nested = LibraryPathMapping.Parse($"{tv}=/media\n{movies}=/media/movies");
        Check(LibraryPathMapping.ToLibrary(nested, "/media/movies/a.mkv")!.Value.LibraryId == movies, "The longest root wins.");
        Console.WriteLine("PASS: library path mappings (shared vectors, longest root, traversal, duplicates).");
    }

    private static void WebhookPayloads()
    {
        static ArrWebhookEvent Parse(string plugin, string json) { using var d = JsonDocument.Parse(json); return ArrWebhookEvent.Parse(plugin, d.RootElement); }
        Check(Parse("sonarr", """{"eventType":"Test"}""").Kind == ArrWebhookKind.Test, "Test events are recognised.");
        var sonarr = Parse("sonarr", """{"eventType":"Download","series":{"path":"/tv/Show"},"episodeFile":{"path":"/tv/Show/S01/e1.mkv"},"episodeFiles":[{"relativePath":"S01/e2.mkv"},{"path":"/tv/Show/S01/e1.mkv"}]}""");
        Check(sonarr.Kind == ArrWebhookKind.Import && sonarr.RemotePaths.SequenceEqual(["/tv/Show/S01/e1.mkv", "/tv/Show/S01/e2.mkv"]), "Sonarr paths are deduplicated and completed from the series folder.");
        var radarr = Parse("radarr", """{"eventType":"Download","isUpgrade":true,"movie":{"folderPath":"/movies/Film"},"movieFile":{"relativePath":"film.mkv"}}""");
        Check(radarr.Kind == ArrWebhookKind.Import && radarr.RemotePaths.Single() == "/movies/Film/film.mkv", "Radarr paths are completed from the movie folder.");
        Check(Parse("radarr", """{"eventType":"Grab"}""").Kind == ArrWebhookKind.Ignored, "Other events are ignored.");
        Check(Parse("sonarr", """{"eventType":"Download"}""").Kind == ArrWebhookKind.Ignored, "An import without a file is ignored.");
        var invalid = false;
        try { Parse("sonarr", "[]"); } catch (FormatException) { invalid = true; }
        Check(invalid, "A non-event body is rejected.");
        Console.WriteLine("PASS: Sonarr/Radarr webhook payload parsing.");
    }

    /// <summary>Le serveur charge les manifestes livrés (Sonarr, Radarr) et celui d’un plugin tiers copié de l’exemple.</summary>
    private static void CatalogAcceptsManifests()
    {
        var plugins = Path.Combine(RepositoryRoot(), "backend", "plugins");
        var directory = Path.Combine(Path.GetTempPath(), "encoding-plugin-manifests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(plugins, "example", "manifest.json"), Path.Combine(directory, "example.json"));
            var registry = new FlowComponentRegistry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FlowPlugins:ManifestDirectory"] = directory }).Build());
            Check(registry.Plugins.Single(plugin => plugin.Id == "example").Components.Count == 2, "The server catalog accepts the third-party manifest.");
            foreach (var id in new[] { "sonarr", "radarr" })
            {
                var plugin = registry.Plugins.Single(item => item.Id == id);
                Check(plugin.ConfigurationSchema!.Fields.Any(field => field.Id == "webhookSecret" && field.Type == "secret")
                    && plugin.ConfigurationSchema.Fields.Any(field => field.Id == "pathMappings"), id + " exposes the trigger settings.");
            }
            foreach (var id in new[] { "plex", "jellyfin" })
                Check(registry.Plugins.Single(item => item.Id == id).ConfigurationSchema!.Fields.Any(field => field.Id == "pathMappings"), id + " exposes the path mapping.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: server catalog accepts the shipped and the example plugin manifests.");
    }

    private static void InvalidManifestsAreRejected()
    {
        var source=Path.Combine(RepositoryRoot(),"backend","plugins","example","manifest.json");
        var root=JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        var duplicate=root.DeepClone().AsObject();
        duplicate["components"]!.AsArray().Add(duplicate["components"]![0]!.DeepClone());
        var unsupported=root.DeepClone().AsObject();
        unsupported["supportedHostContractVersions"] = new JsonArray(2);
        var badId=root.DeepClone().AsObject();
        badId["id"]="../evil";
        var shadowsBuiltIn=root.DeepClone().AsObject();
        shadowsBuiltIn["id"]="plex";
        var badVersion=root.DeepClone().AsObject();
        badVersion["version"]="latest";
        var badSchema=root.DeepClone().AsObject();
        badSchema["components"]![0]!["configurationSchema"]!["fields"]![0]!["type"]="telepathy";
        var cases=new Dictionary<string,string>
        {
            ["unsupported host contract"]=unsupported.ToJsonString(),
            ["duplicate component identity"]=duplicate.ToJsonString(),
            ["unsafe plugin id"]=badId.ToJsonString(),
            ["id of a built-in plugin"]=shadowsBuiltIn.ToJsonString(),
            ["non semantic version"]=badVersion.ToJsonString(),
            ["unknown field type"]=badSchema.ToJsonString(),
            ["unreadable JSON"]="{ not json"
        };

        foreach(var (name,manifest) in cases)
        {
            var directory=Path.Combine(Path.GetTempPath(),"encoding-plugin-invalid-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory,"invalid.json"),manifest);
                var good=JsonNode.Parse(File.ReadAllText(source))!.AsObject();
                good["id"]="second-example";
                foreach(var component in good["components"]!.AsArray()) component!["id"]=component["id"]!.GetValue<string>().Replace("example-","second-");
                File.WriteAllText(Path.Combine(directory,"b-valid.json"),good.ToJsonString());
                var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["FlowPlugins:ManifestDirectory"]=directory }).Build();
                var registry=new FlowComponentRegistry(configuration);
                Check(registry.Rejected.Count==1 && registry.Rejected[0].File=="invalid.json" && registry.Rejected[0].Reason.Length>0,"The catalog reports the invalid manifest without failing: "+name);
                Check(registry.Plugins.All(plugin=>plugin.Id!="../evil") && registry.Plugins.Any(plugin=>plugin.Id=="second-example"),"A broken manifest does not hide a valid one: "+name);
                Check(registry.Plugins.Count(plugin=>plugin.Id=="plex")==1,"A third-party manifest never replaces a built-in plugin: "+name);
            }
            finally { Directory.Delete(directory,true); }
        }
        Console.WriteLine("PASS: an invalid third-party manifest is reported and skipped, never fatal.");
    }
}
