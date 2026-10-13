using System.Net.Http.Json;
using Encoding.Contracts.Nodes;

namespace Encoding.NodeAgent.Services;

public sealed class NodeHeartbeatService(
    NodeAgentOptions options,
    IToolCapabilityProbe capabilityProbe,
    ILogger<NodeHeartbeatService> logger) : BackgroundService
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent du nœud {NodeName} démarré avec l’identifiant {NodeId}.", options.Name, options.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var capabilities = await capabilityProbe.DiscoverAsync(stoppingToken);
                var heartbeat = new NodeHeartbeatRequest(options.Name, options.Roles, options.MaxConcurrentTasks, capabilities);
                using var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    new Uri(options.ApiBaseUri, $"api/v1/nodes/{options.Id:D}/heartbeat"))
                {
                    Content = JsonContent.Create(heartbeat)
                };
                request.Headers.Add("X-Node-Token", options.ApiToken);

                using var response = await _httpClient.SendAsync(request, stoppingToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Le heartbeat du nœud a reçu HTTP {StatusCode}.", (int)response.StatusCode);
                }
                else
                {
                    logger.LogDebug("Heartbeat envoyé pour le nœud {NodeId}.", options.Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "L’API du cluster est momentanément indisponible ; nouvel essai au prochain heartbeat.");
            }
            catch (TaskCanceledException exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Le heartbeat a dépassé son délai ; nouvel essai au prochain cycle.");
            }

            await Task.Delay(options.HeartbeatInterval, stoppingToken);
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
