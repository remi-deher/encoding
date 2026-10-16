using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Data.Common;

namespace Encoding.Api.Infrastructure.PeerCluster;

/// <summary>Imports the first server's existing library catalog, flows, drafts and assignments before other peers join.</summary>
public sealed class PeerBusinessStateSeeder(
    DbDataSource dataSource,
    PeerConsensusService consensus,
    PeerClusterOptions options,
    ILogger<PeerBusinessStateSeeder> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Bootstrap)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = consensus.GetStatus();
                if (status.IsLeader && status.HasQuorum)
                {
                    await SeedLibrariesAsync(cancellationToken);
                    await SeedFlowsAsync(cancellationToken);
                    await SeedLibraryWorkflowsAsync(cancellationToken);
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    private async Task SeedLibrariesAsync(CancellationToken cancellationToken)
    {
        var records = new List<PeerLibraryState>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var read = Sql.Command("SELECT id,name,created_at,is_active FROM libraries WHERE NOT is_deleted ORDER BY id", connection))
        await using (var rows = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await rows.ReadAsync(cancellationToken))
            {
                var created = DateTime.SpecifyKind(rows.GetDateTime(2), DateTimeKind.Utc);
                records.Add(new PeerLibraryState(rows.GetGuid(0), rows.GetString(1), new DateTimeOffset(created), rows.GetBoolean(3)));
            }
        }

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consensus.GetLibraryDocument(record.Id) is not null) continue;
            if (!await consensus.ReplicateLibraryAsync(record, record.Id, 0, false, cancellationToken))
                throw new InvalidOperationException($"Le catalogue des bibliothèques n’a pas pu être importé dans Raft pour {record.Id}.");
        }
        logger.LogInformation("Catalogue initial importé dans Raft : {LibraryCount} bibliothèque(s).", records.Count);
    }

    private async Task SeedFlowsAsync(CancellationToken cancellationToken)
    {
        var versions = new List<PeerFlowVersionState>();
        var drafts = new List<PeerFlowDraftState>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var read = Sql.Command("SELECT flow_id,version,definition::text,created_at FROM flow_versions ORDER BY flow_id,version", connection))
        await using (var rows = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await rows.ReadAsync(cancellationToken))
            {
                var definition = System.Text.Json.JsonSerializer.Deserialize<FlowDefinition>(rows.GetString(2), FlowRepository.JsonOptions)
                    ?? throw new InvalidDataException("Une version de flow locale est illisible.");
                var created = rows.GetFieldValue<DateTimeOffset>(3);
                versions.Add(new PeerFlowVersionState(definition, created));
            }
        }
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var read = Sql.Command("SELECT d.flow_id,d.revision,f.current_version,d.definition::text,d.updated_at FROM flow_drafts d JOIN flows f ON f.id=d.flow_id ORDER BY d.flow_id", connection))
        await using (var rows = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await rows.ReadAsync(cancellationToken))
            {
                var definition = System.Text.Json.JsonSerializer.Deserialize<FlowDefinition>(rows.GetString(3), FlowRepository.JsonOptions)
                    ?? throw new InvalidDataException("Un brouillon de flow local est illisible.");
                drafts.Add(new PeerFlowDraftState(rows.GetGuid(0), rows.GetInt32(1), rows.GetInt32(2), definition,
                    rows.GetFieldValue<DateTimeOffset>(4), true));
            }
        }

        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consensus.GetFlowVersion(version.Definition.Id, version.Definition.Version) is not null) continue;
            if (!await consensus.ReplicateFlowVersionAsync(version, cancellationToken))
                throw new InvalidOperationException($"La version {version.Definition.Version} du flow {version.Definition.Id} n’a pas pu être importée dans Raft.");
        }
        foreach (var draft in drafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consensus.GetFlowDraft(draft.FlowId) is not null) continue;
            if (!await consensus.ReplicateFlowDraftAsync(draft, 0, cancellationToken))
                throw new InvalidOperationException($"Le brouillon du flow {draft.FlowId} n’a pas pu être importé dans Raft.");
        }
        logger.LogInformation("État initial importé dans Raft : {FlowVersionCount} version(s) et {DraftCount} brouillon(s).", versions.Count, drafts.Count);
    }

    private async Task SeedLibraryWorkflowsAsync(CancellationToken cancellationToken)
    {
        var assignments = new List<PeerLibraryWorkflowState>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var read = Sql.Command("""
            SELECT l.id,l.flow_assignment_revision,a.flow_id,a.flow_version
            FROM libraries l LEFT JOIN library_flow_assignments a ON a.library_id=l.id
            WHERE NOT l.is_deleted AND (l.flow_assignment_revision>0 OR a.library_id IS NOT NULL) ORDER BY l.id
            """, connection))
        await using (var rows = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await rows.ReadAsync(cancellationToken))
                assignments.Add(new PeerLibraryWorkflowState(rows.GetGuid(0), rows.IsDBNull(2) ? null : rows.GetGuid(2),
                    rows.IsDBNull(3) ? null : rows.GetInt32(3), rows.GetInt32(1)));
        }

        foreach (var assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consensus.GetLibraryWorkflow(assignment.LibraryId) is not null) continue;
            if (!await consensus.ReplicateLibraryWorkflowAsync(assignment, 0, cancellationToken))
                throw new InvalidOperationException($"L’assignation de flow pour la bibliothèque {assignment.LibraryId} n’a pas pu être importée dans Raft.");
        }
        logger.LogInformation("Assignations initiales de flow importées dans Raft : {AssignmentCount} bibliothèque(s).", assignments.Count);
    }
}
