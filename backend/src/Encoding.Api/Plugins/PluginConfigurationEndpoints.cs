using Encoding.Api.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Plugins;

public static class PluginConfigurationEndpoints
{
    public static void MapPluginConfigurationEndpoints(this WebApplication app)
    {
        var plugins = app.MapGroup("/api/v1/plugins").WithTags("Plugins");
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
    }
}
