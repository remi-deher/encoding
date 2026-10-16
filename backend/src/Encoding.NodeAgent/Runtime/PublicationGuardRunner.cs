using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

/// <summary>Décision des gardes de publication : seul « autorisé » permet de remplacer la source ; sinon <see cref="Detail"/> explique pourquoi elle reste intacte.</summary>
public sealed record PublicationGuardVerdict(bool Allowed, string? Detail)
{
    public static readonly PublicationGuardVerdict Allow = new(true, null);
}

/// <summary>Exécute le composant d'une garde et rend la sortie choisie. Une exception signifie que la garde est indisponible.</summary>
public interface IPublicationGuardInvoker
{
    Task<string> InvokeAsync(RuntimeTransfer transfer, RuntimePublicationGuard guard, CancellationToken ct);
}

/// <summary>Invoque le composant du plugin dans son processus isolé, comme pour une étape de flow (architecture 0059).</summary>
public sealed class PluginPublicationGuardInvoker(PluginProcessHost plugins, NodeAgentOptions options, WorkerIdentityStore identities) : IPublicationGuardInvoker
{
    public async Task<string> InvokeAsync(RuntimeTransfer transfer, RuntimePublicationGuard guard, CancellationToken ct)
    {
        var identity = await identities.GetAsync(ct);
        var workspace = Path.Combine(options.DataDirectory, "transfers", transfer.ExecutionId.ToString("D"), "guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var invocation = new FlowStepInvocation(1, Guid.NewGuid(), transfer.ExecutionId, 1, identity.Id, transfer.Epoch, "publication-guard",
                new(guard.PluginId, guard.PluginVersion, guard.ComponentId, guard.ComponentVersion, guard.ConfigurationSchemaVersion), guard.Configuration,
                new(null, null, null, null, new Dictionary<string, JsonElement>(), transfer.MediaPath, transfer.LibraryId),
                DateTimeOffset.UtcNow.AddSeconds(60), guard.PluginConfiguration);
            var result = await plugins.ExecuteAsync(invocation, workspace, ct);
            return result.OutputId ?? throw new IOException("La garde n'a pas choisi de sortie.");
        }
        finally { try { Directory.Delete(workspace, true); } catch (IOException) { } }
    }
}

/// <summary>
/// Consulte les gardes de publication avant un remplacement de source (architecture 0059). Toutes doivent autoriser. Une garde qui
/// demande d'attendre est réinterrogée toutes les 30 secondes jusqu'à son attente maximale ; une garde indisponible ou une sortie inattendue
/// refuse : en cas de doute la source n'est jamais remplacée (le résultat distinct reste publié).
/// </summary>
public sealed class PublicationGuardRunner(IPublicationGuardInvoker invoker, ILogger<PublicationGuardRunner> logger)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public Task<PublicationGuardVerdict> CheckAsync(RuntimeTransfer transfer, CancellationToken ct) =>
        EvaluateAsync(transfer, invoker, (delay, token) => Task.Delay(delay, token), () => DateTimeOffset.UtcNow, logger, ct);

    public static async Task<PublicationGuardVerdict> EvaluateAsync(RuntimeTransfer transfer, IPublicationGuardInvoker invoker,
        Func<TimeSpan, CancellationToken, Task> delay, Func<DateTimeOffset> clock, ILogger logger, CancellationToken ct)
    {
        if (transfer.Guards is not { Count: > 0 } guards) return PublicationGuardVerdict.Allow;
        var started = clock();
        while (true)
        {
            var waiting = new List<RuntimePublicationGuard>();
            foreach (var guard in guards)
            {
                string output;
                try { output = await invoker.InvokeAsync(transfer, guard, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning("Garde {Plugin} indisponible pour {ExecutionId} : {Type}.", guard.PluginId, transfer.ExecutionId, exception.GetType().Name);
                    return new(false, $"Remplacement non effectué : la garde du plugin {guard.PluginId} est indisponible.");
                }
                if (guard.AllowOutputs.Contains(output)) continue;
                if (guard.WaitOutputs.Contains(output)) { waiting.Add(guard); continue; }
                return new(false, $"Remplacement non effectué : la garde du plugin {guard.PluginId} a refusé (sortie « {output} »).");
            }
            if (waiting.Count == 0) return PublicationGuardVerdict.Allow;
            var elapsed = clock() - started;
            var expired = waiting.FirstOrDefault(guard => elapsed + PollInterval > TimeSpan.FromMinutes(guard.MaxWaitMinutes));
            if (expired is not null)
                return new(false, $"Remplacement non effectué : le plugin {expired.PluginId} demande d'attendre au-delà de {expired.MaxWaitMinutes} min ; la source est conservée.");
            await delay(PollInterval, ct);
        }
    }
}
