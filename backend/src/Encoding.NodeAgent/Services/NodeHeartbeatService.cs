using System.Net.Http.Json;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;
using System.Runtime.InteropServices;

namespace Encoding.NodeAgent.Services;

public sealed class NodeHeartbeatService(
    NodeAgentOptions options,
    WorkerIdentityStore identityStore,
    IToolCapabilityProbe capabilityProbe,
    ILogger<NodeHeartbeatService> logger,
    LauncherControlClient? launcher = null) : BackgroundService
{
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = await identityStore.GetAsync(stoppingToken);
        logger.LogInformation("Agent worker {NodeName} démarré avec l’identifiant local {NodeId}.", options.Name, identity.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                identity = await identityStore.GetAsync(stoppingToken);
                if (identity.HasCredentials)
                {
                    var capabilities = await capabilityProbe.DiscoverAsync(stoppingToken);
                    var memoryMb = Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
                    var inventory = new NodeInventory(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, memoryMb);
                    var heartbeat = new NodeHeartbeatRequest(options.Name, options.Roles, options.MaxConcurrentTasks, capabilities, inventory);
                    using var request = new HttpRequestMessage(HttpMethod.Put,
                        new Uri(new Uri(identity.ApiBaseUrl!), $"api/v1/nodes/{identity.Id:D}/heartbeat"))
                    {
                        Content = JsonContent.Create(heartbeat)
                    };
                    request.Headers.Add("X-Node-Token", identity.NodeToken);
                    using var response = await httpClient.SendAsync(request, stoppingToken);
                    if (!response.IsSuccessStatusCode && !(response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !identity.IsEnrolled))
                        logger.LogWarning("Le heartbeat du nœud {NodeId} a reçu HTTP {StatusCode}.", identity.Id, (int)response.StatusCode);
                    else if (response.IsSuccessStatusCode)
                    {
                        if (!identity.IsEnrolled)
                        {
                            await identityStore.MarkEnrolledAsync(stoppingToken);
                            logger.LogInformation("L’enrôlement en attente du worker {NodeId} a été confirmé par le cluster.", identity.Id);
                        }
                        logger.LogDebug("Heartbeat envoyé pour le nœud {NodeId}.", identity.Id);
                        var node = await response.Content.ReadFromJsonAsync<NodeResponse>(stoppingToken);
                        await ApplyDesiredModeAsync(node?.DesiredRoles, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Le cluster est momentanément indisponible ; nouvel essai au prochain heartbeat.");
            }
            catch (TaskCanceledException exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Le heartbeat a dépassé son délai ; nouvel essai au prochain cycle.");
            }

            await Task.Delay(options.HeartbeatInterval, stoppingToken);
        }
    }

    // Mode demandé par un administrateur du cluster (0036) : appliqué par le lanceur local, qui relance l'agent avec ses nouveaux rôles.
    private async Task ApplyDesiredModeAsync(IReadOnlyList<NodeRole>? desiredRoles, CancellationToken cancellationToken)
    {
        if (desiredRoles is not { Count: > 0 }) return;
        var desired = new NodeMode(desiredRoles.Contains(NodeRole.Server), desiredRoles.Contains(NodeRole.Worker));
        if (desired == new NodeMode(options.Roles.Contains(NodeRole.Server), options.Roles.Contains(NodeRole.Worker))) return;
        if (launcher is null)
        {
            logger.LogWarning("Le cluster demande le mode {Mode}, mais ce nœud n'est pas géré par un lanceur (image unique) ; demande ignorée.", desired.ToRoles());
            return;
        }
        try
        {
            await launcher.SetAsync(desired, cancellationToken);
            logger.LogInformation("Mode du nœud changé à la demande du cluster : {Mode}.", desired.ToRoles());
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Le lanceur a refusé le changement de mode demandé par le cluster.");
        }
    }

    public override void Dispose()
    {
        httpClient.Dispose();
        base.Dispose();
    }
}
