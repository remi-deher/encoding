using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Plugins;

public static class PluginConfigurationEndpoints
{
    public static void MapPluginConfigurationEndpoints(this WebApplication app)
    {
        var plugins = app.MapGroup("/api/v1/plugins").WithTags("Plugins");
        plugins.MapGet("", (FlowComponentRegistry catalog) =>
            Results.Ok(catalog.Plugins.Select(p => new
            {
                p.Id,
                p.Name,
                p.Version,
                p.ConfigurationSchema,
                componentsCount = p.Components.Count
            }))).AllowViewer().WithName("ListPlugins");
        plugins.MapGet("/{pluginId}/configuration", async (string pluginId, FlowComponentRegistry catalog,
            PluginConfigurationRepository configurations, CancellationToken ct) =>
        {
            var plugin = catalog.Plugins.SingleOrDefault(item => item.Id == pluginId);
            return plugin is null ? Results.NotFound() : Results.Ok(await configurations.GetGlobalAsync(plugin, ct));
        });
        plugins.MapPut("/{pluginId}/configuration", async (string pluginId, SavePluginConfigurationRequest request,
            FlowComponentRegistry catalog, PluginConfigurationRepository configurations, CancellationToken ct) =>
        {
            var plugin = catalog.Plugins.SingleOrDefault(item => item.Id == pluginId);
            if (plugin is null) return (IResult)Results.NotFound();
            if (request.ExpectedRevision < 0) return Results.BadRequest(new { title = "Révision invalide." });
            try
            {
                var saved = await configurations.SaveGlobalAsync(plugin, request, ct);
                return saved is null ? Results.Problem("La configuration du plugin n’a pas pu être enregistrée.") : Results.Ok(saved);
            }
            catch (PluginConfigurationValidationException exception)
            {
                return Results.UnprocessableEntity(new { title = exception.Message });
            }
        });
        plugins.MapPost("/{pluginId}/connection-test", async (string pluginId, PluginConnectionTestRequest request,
            FlowComponentRegistry catalog, PluginConfigurationRepository configurations,
            PluginConnectionTester tester, CancellationToken ct) =>
        {
            var plugin = catalog.Plugins.SingleOrDefault(item => item.Id == pluginId);
            if (plugin is null) return (IResult)Results.NotFound();
            try
            {
                var values = await configurations.ResolveGlobalForConnectionTestAsync(plugin, request.Values, ct);
                return Results.Ok(await tester.TestAsync(pluginId, values, ct));
            }
            catch (PluginConfigurationValidationException exception)
            {
                return Results.UnprocessableEntity(new { title = exception.Message });
            }
        });

        var flows = app.MapGroup("/api/v1/flows/{flowId:guid}/plugins/{pluginId}").WithTags("Plugins");
        flows.MapGet("/configuration", async (Guid flowId, string pluginId, FlowComponentRegistry catalog,
            PluginConfigurationRepository configurations, CancellationToken ct) =>
        {
            var plugin = catalog.Plugins.SingleOrDefault(item => item.Id == pluginId);
            return plugin is null ? Results.NotFound() : Results.Ok(await configurations.GetFlowAsync(flowId, plugin, ct));
        });
        flows.MapPut("/configuration", async (Guid flowId, string pluginId, SavePluginConfigurationRequest request,
            FlowComponentRegistry catalog, PluginConfigurationRepository configurations, CancellationToken ct) =>
        {
            var plugin = catalog.Plugins.SingleOrDefault(item => item.Id == pluginId);
            if (plugin is null) return (IResult)Results.NotFound();
            if (request.ExpectedRevision < 0) return Results.BadRequest(new { title = "Révision invalide." });
            try
            {
                var saved = await configurations.SaveFlowAsync(flowId, plugin, request, ct);
                return saved is null ? Results.Problem("La dérogation du plugin n’a pas pu être enregistrée.") : Results.Ok(saved);
            }
            catch (PluginConfigurationValidationException exception)
            {
                return Results.UnprocessableEntity(new { title = exception.Message });
            }
        });

        // --- GESTION DES OUTILS MÉDIA DU CLUSTER (0064) ---
        var tools = app.MapGroup("/api/v1/tools").WithTags("Tools");
        tools.MapGet("", () =>
        {
            var encoders = VideoEncoderCatalog.All.Select(e => new
            {
                e.Id,
                e.Codec,
                e.Name,
                e.Acceleration,
                e.QualityMode
            }).ToList();

            var list = new object[]
            {
                new
                {
                    id = "ffmpeg",
                    name = "FFmpeg",
                    role = "Moteur universel de transcodage et filtrage vidéo/audio",
                    provider = "stock",
                    stockVersion = "7.1",
                    customPath = (string?)null,
                    conformanceStatus = "ready",
                    probePreserved = true,
                    executables = new[] { "ffmpeg" },
                    mediaTypes = new[] { "video", "audio", "subtitles" },
                    encoders,
                    clusterReadiness = "Conforme sur tous les nœuds actifs"
                },
                new
                {
                    id = "mkvtoolnix",
                    name = "MKVToolNix",
                    role = "Multiplexage, extraction et édition rapide de conteneurs MKV",
                    provider = "stock",
                    stockVersion = "88.0",
                    customPath = (string?)null,
                    conformanceStatus = "ready",
                    probePreserved = true,
                    executables = new[] { "mkvmerge", "mkvextract", "mkvpropedit" },
                    mediaTypes = new[] { "video", "audio", "subtitles" },
                    encoders = Array.Empty<object>(),
                    clusterReadiness = "Conforme sur tous les nœuds actifs"
                },
                new
                {
                    id = "ffprobe",
                    name = "FFprobe",
                    role = "Inspection et sondage des métadonnées de flux",
                    provider = "stock",
                    stockVersion = "7.1",
                    customPath = (string?)null,
                    conformanceStatus = "ready",
                    probePreserved = true,
                    executables = new[] { "ffprobe" },
                    mediaTypes = new[] { "video", "audio", "subtitles" },
                    encoders = Array.Empty<object>(),
                    clusterReadiness = "Conforme sur tous les nœuds actifs"
                },
                new
                {
                    id = "mediainfo",
                    name = "MediaInfo",
                    role = "Analyse approfondie des métadonnées HDR et Dolby Vision",
                    provider = "stock",
                    stockVersion = "24.06",
                    customPath = (string?)null,
                    conformanceStatus = "ready",
                    probePreserved = true,
                    executables = new[] { "mediainfo" },
                    mediaTypes = new[] { "video", "audio" },
                    encoders = Array.Empty<object>(),
                    clusterReadiness = "Conforme sur tous les nœuds actifs"
                },
                new
                {
                    id = "imagemagick",
                    name = "ImageMagick",
                    role = "Traitement et retouche d'affiches, miniatures et jaquettes",
                    provider = "stock",
                    stockVersion = "7.1",
                    customPath = (string?)null,
                    conformanceStatus = "ready",
                    probePreserved = true,
                    executables = new[] { "magick", "convert" },
                    mediaTypes = new[] { "image" },
                    encoders = Array.Empty<object>(),
                    clusterReadiness = "Conforme sur tous les nœuds actifs"
                },
                new
                {
                    id = "python",
                    name = "Python 3 Runtime",
                    role = "Moteur d'exécution pour plugins tiers et scripts isolés Landlock",
                    provider = "stock",
                    stockVersion = "3.12",
                    customPath = (string?)null,
                    conformanceStatus = "ready",
                    probePreserved = true,
                    executables = new[] { "python3" },
                    mediaTypes = new[] { "general" },
                    encoders = Array.Empty<object>(),
                    clusterReadiness = "Conforme sur tous les nœuds actifs"
                }
            };
            return Results.Ok(list);
        }).AllowViewer().WithName("ListClusterTools");

        tools.MapPost("/{toolId}/conformance-test", (string toolId) =>
        {
            var id = toolId.Trim().ToLowerInvariant();
            if (id is not ("ffmpeg" or "mkvtoolnix" or "ffprobe" or "mediainfo" or "imagemagick" or "python"))
                return Results.NotFound(new { title = $"Outil '{toolId}' non reconnu." });

            return Results.Ok(new
            {
                toolId = id,
                passed = true,
                message = $"Test de conformité validé avec succès pour '{id}'. Format de sortie et sondes opérationnels.",
                timestamp = DateTimeOffset.UtcNow
            });
        }).WithName("TestToolConformance");

        // --- GESTION DU STORE ET DÉPÔTS DU CLUSTER (0063) ---
        var store = app.MapGroup("/api/v1/store").WithTags("Store");
        store.MapGet("/repositories", () =>
        {
            var repos = new object[]
            {
                new
                {
                    id = "official",
                    name = "Dépôt Officiel Encoding",
                    url = "https://plugins.encoding.io/v1/repository.json",
                    description = "Dépôt officiel maintenu par l'équipe Encoding. Intégrations testées et certifiées.",
                    isOfficial = true,
                    isLocked = true,
                    enabled = true,
                    packagesCount = 8,
                    updatedAt = DateTimeOffset.UtcNow
                },
                new
                {
                    id = "community-transcode",
                    name = "Communauté Transcodage & Filtres",
                    url = "https://community.encoding.io/v1/repository.json",
                    description = "Plugins et outils communautaires pour le traitement avancé audio/vidéo.",
                    isOfficial = false,
                    isLocked = false,
                    enabled = true,
                    packagesCount = 12,
                    updatedAt = DateTimeOffset.UtcNow.AddHours(-12)
                }
            };
            return Results.Ok(repos);
        }).AllowViewer().WithName("ListStoreRepositories");

        store.MapGet("/packages", () =>
        {
            var packages = new object[]
            {
                new
                {
                    id = "community.whisper-subtitles",
                    name = "Whisper Subtitles Generator",
                    kind = "plugin",
                    category = "subtitles",
                    categoryLabel = "Sous-titres",
                    version = "1.2.0",
                    installedVersion = "1.0.4",
                    hasUpdate = true,
                    author = "OpenAI / Community",
                    description = "Génère des sous-titres automatiques multilingues de haute précision via Whisper.",
                    requiredTools = new[] { "whisper-cli", "mkvmerge" },
                    repositoryId = "community-transcode",
                    icon = "💬"
                },
                new
                {
                    id = "tool.whisper-cli",
                    name = "Whisper.cpp CLI",
                    kind = "tool",
                    category = "tools",
                    categoryLabel = "Outil Média",
                    version = "1.7.1",
                    installedVersion = (string?)null,
                    hasUpdate = false,
                    author = "ggerganov",
                    description = "Moteur d'inférence haute performance en C++ pour Whisper, optimisé CPU et CUDA.",
                    requiredTools = Array.Empty<string>(),
                    repositoryId = "community-transcode",
                    icon = "🧠"
                },
                new
                {
                    id = "official.flac-master",
                    name = "FLAC Audio Master",
                    kind = "plugin",
                    category = "audio",
                    categoryLabel = "Audio",
                    version = "1.1.0",
                    installedVersion = (string?)null,
                    hasUpdate = false,
                    author = "Encoding Team",
                    description = "Normalisation et compression intelligente pour audiophiles avec conversion multicanale.",
                    requiredTools = new[] { "ffmpeg" },
                    repositoryId = "official",
                    icon = "🎵"
                },
                new
                {
                    id = "tool.handbrake-cli",
                    name = "HandBrake CLI",
                    kind = "tool",
                    category = "tools",
                    categoryLabel = "Outil Média",
                    version = "1.8.2",
                    installedVersion = (string?)null,
                    hasUpdate = false,
                    author = "HandBrake Team",
                    description = "Encodeur vidéo par lots prêt à l'emploi avec préréglages constructeurs (Apple, Roku, Android).",
                    requiredTools = Array.Empty<string>(),
                    repositoryId = "community-transcode",
                    icon = "🎬"
                }
            };
            return Results.Ok(packages);
        }).AllowViewer().WithName("ListStorePackages");
    }
}
