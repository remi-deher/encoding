using Encoding.Api.Auth;
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
        plugins.MapGet("/rejected", (FlowComponentRegistry catalog) => Results.Ok(catalog.Rejected)).AllowViewer().WithName("ListRejectedPlugins");
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

        // --- OUTILS MÉDIA DU CLUSTER (0064) ---
        // Le serveur ne sonde pas les outils : la version et la conformité réelles sont annoncées par chaque nœud
        // (capacités, ToolCapabilityProbe). Cette liste décrit seulement les outils livrés ; aucun état n'est inventé.
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

            static object Tool(string id, string name, string role, string[] executables, string[] mediaTypes, object encoders) => new
            {
                id,
                name,
                role,
                provider = "stock",
                stockVersion = "",
                customPath = (string?)null,
                conformanceStatus = "unknown",
                probePreserved = true,
                executables,
                mediaTypes,
                encoders,
                clusterReadiness = "Version et état annoncés par chaque nœud"
            };
            var none = Array.Empty<object>();
            var list = new object[]
            {
                Tool("ffmpeg", "FFmpeg", "Moteur universel de transcodage et filtrage vidéo/audio", ["ffmpeg"], ["video", "audio", "subtitles"], encoders),
                Tool("mkvtoolnix", "MKVToolNix", "Multiplexage, extraction et édition rapide de conteneurs MKV", ["mkvmerge", "mkvextract", "mkvpropedit"], ["video", "audio", "subtitles"], none),
                Tool("ffprobe", "FFprobe", "Inspection et sondage des métadonnées de flux", ["ffprobe"], ["video", "audio", "subtitles"], none),
                Tool("mediainfo", "MediaInfo", "Analyse approfondie des métadonnées HDR et Dolby Vision", ["mediainfo"], ["video", "audio"], none),
                Tool("imagemagick", "ImageMagick", "Traitement et retouche d'affiches, miniatures et jaquettes", ["magick", "convert"], ["image"], none),
                Tool("python", "Python 3", "Moteur d'exécution des plugins tiers et des scripts isolés Landlock", ["python3"], ["general"], none)
            };
            return Results.Ok(list);
        }).AllowViewer().WithName("ListClusterTools");

        // Aucun test de conformité n'existe encore : répondre « réussi » sans rien exécuter serait une fausse assurance.
        tools.MapPost("/{toolId}/conformance-test", (string toolId) =>
            Results.Json(new { title = "Le test de conformité des outils n’est pas encore disponible." },
                statusCode: StatusCodes.Status501NotImplemented)).WithName("TestToolConformance");
    }
}
