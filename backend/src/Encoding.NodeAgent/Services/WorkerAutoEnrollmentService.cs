namespace Encoding.NodeAgent.Services;

public sealed class WorkerAutoEnrollmentService(
    NodeAgentOptions options,
    WorkerIdentityStore identityStore,
    WorkerClusterDiscovery discovery,
    WorkerEnrollmentService enrollment,
    ILogger<WorkerAutoEnrollmentService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.AutomaticEnrollmentEnabled)
        {
            logger.LogInformation("L’acceptation automatique des enrôlements est désactivée.");
            return;
        }

        logger.LogInformation("L’acceptation automatique est activée ; un seul cluster détecté sera accepté automatiquement.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await identityStore.GetAsync(stoppingToken);
                var hasActivePairing = identity.PairingExpiresAt > DateTimeOffset.UtcNow
                    && !string.IsNullOrWhiteSpace(identity.PairingKey);
                var clusters = discovery.List();
                if (!identity.IsEnrolled && !hasActivePairing && clusters.Count == 1)
                {
                    await enrollment.EnrollAsync(clusters[0].Id, stoppingToken);
                    logger.LogInformation("L’enrôlement automatique auprès du cluster {ClusterId} est terminé.", clusters[0].Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "L’enrôlement automatique n’a pas abouti ; une nouvelle tentative sera faite.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
