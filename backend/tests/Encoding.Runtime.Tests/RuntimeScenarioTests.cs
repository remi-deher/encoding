using Xunit;

namespace Encoding.Runtime.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RuntimeScenarioTests(PostgresFixture database)
{
    private Harness.Fixture Scenarios => database.Scenarios ?? throw new InvalidOperationException("The PostgreSQL fixture is not initialized.");

    [PostgresFact]
    public Task FlowSavingIsIdempotentAndConcurrencySafe() => Scenarios.FlowSavingAsync();

    [PostgresFact]
    public Task DurableRelayBeforeTransformation() => Scenarios.BackendRelayAsync(false);

    [PostgresFact]
    public Task DurableRelayAfterTransformation() => Scenarios.BackendRelayAsync(true);

    [PostgresFact]
    public Task ConfigurationSnapshotsStayFixedAndForgedTargetsAreRejected() => Scenarios.FrozenConfigurationAsync();

    [PostgresFact]
    public Task MissingOrCorruptArtifactsAndUnavailableTargetsAreRefused() => Scenarios.IntegrityAndAvailabilityAsync();

    [PostgresFact]
    public Task ConcurrentReceiverClaimsKeepOneOwner() => Scenarios.ConcurrentRelayAsync();

    [PostgresFact]
    public Task EarlyWorkerRelayAvoidsDownloadsAndSurvivesReceiverLoss() => Scenarios.EarlyRunnerAsync();

    [PostgresFact]
    public Task CancellationReleasesTheDurableReservation() => Scenarios.CancellationAsync();

    [PostgresFact]
    public Task ConditionalRelayYesBranch() => Scenarios.ConditionalRelayAsync("yes");

    [PostgresFact]
    public Task ConditionalRelayNoBranch() => Scenarios.ConditionalRelayAsync("no");

    [PostgresFact]
    public Task ConditionalRelayErrorBranch() => Scenarios.ConditionalRelayAsync("error");

    [MediaToolsFact]
    public Task H265ConversionHandedOffAfterExecution() => Scenarios.MediaRelayAsync(false);

    [MediaToolsFact]
    public Task H265PlanHandedOffBeforeExecution() => Scenarios.MediaRelayAsync(true);

    [PostgresFact]
    public Task OperationsMigrationsHealthAndArtifactRetention() => OperationsScenarios.RunDatabaseAsync(PostgresFixture.ConnectionString!);

    [PostgresFact]
    public Task SourceReplacementLifecycle() => Scenarios.ReplacementLifecycleAsync();

    [PostgresFact]
    public Task WorkerLossFailover() => Scenarios.WorkerLossFailoverAsync();

    [PostgresFact]
    public Task ServerLossFailover() => Scenarios.ServerLossFailoverAsync();

    [MediaToolsFact]
    public Task H265SourceReplacedThenRestored() => Scenarios.MediaRelayAsync(false, true);
}
