using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Replication;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerConsensusStatus(bool IsLeader, string? LeaderEndpoint, IReadOnlyList<string> Members,
    long LastEntryIndex, long LastCommittedEntryIndex, bool HasQuorum);

public sealed class PeerConsensusService(IRaftHttpCluster cluster, PeerStateMachine stateMachine)
{
    private static readonly TimeSpan ConsensusOperationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MembershipOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions BusinessJson = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim businessWriteGate = new(1, 1);
    private readonly SemaphoreSlim membershipChangeGate = new(1, 1);

    public PeerConsensusStatus GetStatus()
    {
        var raftCluster = (IRaftCluster)cluster;
        var hasQuorum = raftCluster.TryGetLeaseToken(out var leaseToken) && !leaseToken.IsCancellationRequested;
        return new PeerConsensusStatus(cluster.Leader?.IsLeader == true, cluster.Leader?.EndPoint.ToString(),
            raftCluster.Members.Select(member => member.EndPoint.ToString() ?? string.Empty).Order(StringComparer.Ordinal).ToArray(),
            raftCluster.AuditTrail.LastEntryIndex, raftCluster.AuditTrail.LastCommittedEntryIndex, hasQuorum);
    }

    public bool TryGetLeaderLease(out CancellationToken leaseToken) =>
        ((IRaftCluster)cluster).TryGetLeaseToken(out leaseToken) && !leaseToken.IsCancellationRequested;

    public bool IsPeerRevoked(Guid peerId) => Get($"peer/revoked/{peerId:D}") is { ValueKind: JsonValueKind.True };

    public Task SetPeerRevokedAsync(Guid peerId, bool revoked, CancellationToken cancellationToken) =>
        ReplicateAsync(new PeerReplicatedStateCommand(Guid.NewGuid(), $"peer/revoked/{peerId:D}",
            JsonSerializer.SerializeToElement(revoked)), cancellationToken);

    public bool IsMember(Uri endpoint) => ((IRaftCluster)cluster).Members.Any(member =>
        string.Equals(Normalize(member.EndPoint.ToString()), Normalize(endpoint.ToString()), StringComparison.OrdinalIgnoreCase));

    public async Task<bool> AddMemberAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        await membershipChangeGate.WaitAsync(cancellationToken);
        try
        {
            var status = GetStatus();
            if (!status.IsLeader || !status.HasQuorum) return false;
            if (IsMember(endpoint)) return true;

            var changed = await WithMembershipTimeoutAsync(token => cluster.AddMemberAsync(endpoint, token), cancellationToken);
            if (!changed && !IsMember(endpoint)) return false;
            return await WaitForMembershipAsync(endpoint, isMember: true, cancellationToken);
        }
        finally { membershipChangeGate.Release(); }
    }

    public async Task<bool> RemoveMemberAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        await membershipChangeGate.WaitAsync(cancellationToken);
        try
        {
            var status = GetStatus();
            if (!status.IsLeader || !status.HasQuorum || status.Members.Count < 2) return false;
            if (!IsMember(endpoint)) return true;

            var changed = await WithMembershipTimeoutAsync(token => cluster.RemoveMemberAsync(endpoint, token), cancellationToken);
            if (!changed && IsMember(endpoint)) return false;
            return await WaitForMembershipAsync(endpoint, isMember: false, cancellationToken);
        }
        finally { membershipChangeGate.Release(); }
    }

    private async Task<bool> WaitForMembershipAsync(Uri endpoint, bool isMember, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MembershipOperationTimeout);
        try
        {
            while (IsMember(endpoint) != isMember)
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task ReplicateAsync(PeerReplicatedStateCommand command, CancellationToken cancellationToken)
    {
        var isClusterName = command.Key == "cluster/name" && command.Value.ValueKind == JsonValueKind.String
            && command.Value.GetString() is { Length: > 0 and <= 128 };
        var isPeerRevocation = command.Key.StartsWith("peer/revoked/", StringComparison.Ordinal)
            && Guid.TryParseExact(command.Key.AsSpan("peer/revoked/".Length), "D", out _)
            && command.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var isLibraryState = PeerBusinessState.TryReadLibraryKey(command.Key, out var libraryId)
            && PeerBusinessState.TryParseLibraryDocument(command.Value, out var document)
            && (document.Deleted || document.Value.Deserialize<PeerLibraryState>(BusinessJson)?.Id == libraryId);
        var isFlowVersion = PeerBusinessState.TryReadFlowVersionKey(command.Key, out var flowId, out var version)
            && PeerBusinessState.TryParseFlowVersion(command.Value, out var flowState)
            && flowState.Definition.Id == flowId && flowState.Definition.Version == version;
        var isFlowDraft = PeerBusinessState.TryReadFlowDraftKey(command.Key, out var draftId)
            && PeerBusinessState.TryParseFlowDraft(command.Value, out var draftState)
            && draftState.FlowId == draftId;
        var isLibraryWorkflow = PeerBusinessState.TryReadLibraryWorkflowKey(command.Key, out var workflowLibraryId)
            && PeerBusinessState.TryParseLibraryWorkflow(command.Value, out var workflowState)
            && workflowState.LibraryId == workflowLibraryId;
        if (!isClusterName && !isPeerRevocation && !isLibraryState && !isFlowVersion && !isFlowDraft && !isLibraryWorkflow)
            throw new ArgumentException("La commande de réplication ne correspond pas à une projection autorisée.");
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(command);
        if (bytes.Length > PeerStateMachine.MaximumCommandBytes) throw new ArgumentException("La commande dépasse la limite de quatre mégaoctets.");
        await WithTimeoutAsync(async token =>
        {
            await ((IRaftCluster)cluster).ReplicateAsync((ReadOnlyMemory<byte>)bytes, null, token);
            return true;
        }, cancellationToken);
    }

    public JsonElement? Get(string key) => stateMachine.Get(key);

    public PeerBusinessStateDocument? GetLibraryDocument(Guid id)
    {
        var value = stateMachine.Get(PeerBusinessState.LibraryKey(id));
        if (value is null) return null;
        return PeerBusinessState.TryParseLibraryDocument(value.Value, out var document) ? document : null;
    }

    public IReadOnlyList<PeerLibraryState> ListLibraries(bool includeDeleted = false) => stateMachine.List("business/library/")
        .Select(pair => PeerBusinessState.TryParseLibraryDocument(pair.Value, out var document)
            && (includeDeleted || !document.Deleted) && document.Value.ValueKind == JsonValueKind.Object
            ? document.Value.Deserialize<PeerLibraryState>(BusinessJson) : null)
        .Where(library => library is not null).Select(library => library!).ToArray();

    public async Task<bool> ReplicateLibraryAsync(PeerLibraryState? library, Guid id, long expectedRevision,
        bool deleted, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || expectedRevision < 0 || (!deleted && (library is null || library.Id != id)))
            throw new ArgumentException("La commande de bibliothèque est invalide.");
        await businessWriteGate.WaitAsync(cancellationToken);
        try
        {
            var status = GetStatus();
            if (!status.IsLeader || !status.HasQuorum) return false;
            var current = GetLibraryDocument(id);
            var revision = current?.Revision ?? 0;
            if (revision != expectedRevision) return false;
            if (!deleted && library is not null && ListLibraries(includeDeleted: true).Any(item => item.Id != id
                && string.Equals(item.Name, library.Name, StringComparison.Ordinal))) return false;
            var value = deleted
                ? current?.Value ?? JsonSerializer.SerializeToElement<object?>(null, BusinessJson)
                : JsonSerializer.SerializeToElement(library, BusinessJson);
            var next = new PeerBusinessStateDocument(revision + 1, deleted, value);
            var command = new PeerReplicatedStateCommand(Guid.NewGuid(), PeerBusinessState.LibraryKey(id),
                JsonSerializer.SerializeToElement(next, BusinessJson));
            await ReplicateAsync(command, cancellationToken);
            return true;
        }
        finally { businessWriteGate.Release(); }
    }

    public PeerFlowVersionState? GetFlowVersion(Guid id, int version)
    {
        var value = stateMachine.Get(PeerBusinessState.FlowVersionKey(id, version));
        return value is not null && PeerBusinessState.TryParseFlowVersion(value.Value, out var state) ? state : null;
    }

    public PeerFlowVersionState? GetLatestFlowVersion(Guid id) => stateMachine.List($"business/flow-version/{id:D}/")
        .Select(pair => PeerBusinessState.TryParseFlowVersion(pair.Value, out var state) ? state : null)
        .Where(state => state is not null).OrderByDescending(state => state!.Definition.Version).FirstOrDefault();

    public PeerFlowDraftState? GetFlowDraft(Guid id)
    {
        var value = stateMachine.Get(PeerBusinessState.FlowDraftKey(id));
        return value is not null && PeerBusinessState.TryParseFlowDraft(value.Value, out var state) ? state : null;
    }

    public async Task<bool> ReplicateFlowVersionAsync(PeerFlowVersionState state, CancellationToken cancellationToken)
    {
        var definition = state.Definition;
        if (definition.Id == Guid.Empty || definition.Version <= 0)
            throw new ArgumentException("La version de flow à répliquer est invalide.");
        await businessWriteGate.WaitAsync(cancellationToken);
        try
        {
            var status = GetStatus();
            if (!status.IsLeader || !status.HasQuorum) return false;
            var existing = GetFlowVersion(definition.Id, definition.Version);
            if (existing is not null) return false;
            var latest = GetLatestFlowVersion(definition.Id)?.Definition.Version ?? 0;
            if (definition.Version != latest + 1) return false;
            if (state.ExpectedDraftRevision is { } expectedDraft)
            {
                var draft = GetFlowDraft(definition.Id);
                if (draft is not { HasDraft: true } || draft.Revision != expectedDraft) return false;
            }
            var command = new PeerReplicatedStateCommand(Guid.NewGuid(), PeerBusinessState.FlowVersionKey(definition.Id, definition.Version),
                JsonSerializer.SerializeToElement(state, BusinessJson));
            await ReplicateAsync(command, cancellationToken);
            return true;
        }
        finally { businessWriteGate.Release(); }
    }

    public async Task<bool> ReplicateFlowDraftAsync(PeerFlowDraftState state, int expectedRevision, CancellationToken cancellationToken)
    {
        if (state.FlowId == Guid.Empty || state.Revision <= 0 || (state.HasDraft && state.Definition is null))
            throw new ArgumentException("Le brouillon de flow à répliquer est invalide.");
        await businessWriteGate.WaitAsync(cancellationToken);
        try
        {
            var status = GetStatus();
            if (!status.IsLeader || !status.HasQuorum) return false;
            var current = GetFlowDraft(state.FlowId);
            var activeRevision = current is { HasDraft: true } ? current.Revision : 0;
            if (activeRevision != expectedRevision) return false;
            if (state.HasDraft && state.Revision <= (current?.Revision ?? 0)) return false;
            if (!state.HasDraft && (current is not { HasDraft: true } || state.Revision != current.Revision)) return false;
            var command = new PeerReplicatedStateCommand(Guid.NewGuid(), PeerBusinessState.FlowDraftKey(state.FlowId),
                JsonSerializer.SerializeToElement(state, BusinessJson));
            await ReplicateAsync(command, cancellationToken);
            return true;
        }
        finally { businessWriteGate.Release(); }
    }

    public PeerLibraryWorkflowState? GetLibraryWorkflow(Guid id)
    {
        var value = stateMachine.Get(PeerBusinessState.LibraryWorkflowKey(id));
        return value is not null && PeerBusinessState.TryParseLibraryWorkflow(value.Value, out var state) ? state : null;
    }

    public async Task<bool> ReplicateLibraryWorkflowAsync(PeerLibraryWorkflowState state, int expectedRevision,
        CancellationToken cancellationToken)
    {
        if (state.LibraryId == Guid.Empty || state.Revision <= 0
            || (state.FlowId is null) != (state.FlowVersion is null))
            throw new ArgumentException("L’assignation du flow à la bibliothèque est invalide.");
        await businessWriteGate.WaitAsync(cancellationToken);
        try
        {
            var status = GetStatus();
            if (!status.IsLeader || !status.HasQuorum) return false;
            var current = GetLibraryWorkflow(state.LibraryId);
            if ((current?.Revision ?? 0) != expectedRevision) return false;
            if (current is null ? state.Revision <= 0 : state.Revision != expectedRevision + 1) return false;
            if (state.FlowId is { } flowId && GetFlowVersion(flowId, state.FlowVersion!.Value) is null) return false;
            var command = new PeerReplicatedStateCommand(Guid.NewGuid(), PeerBusinessState.LibraryWorkflowKey(state.LibraryId),
                JsonSerializer.SerializeToElement(state, BusinessJson));
            await ReplicateAsync(command, cancellationToken);
            return true;
        }
        finally { businessWriteGate.Release(); }
    }

    private static string Normalize(string? endpoint) => (endpoint ?? string.Empty).TrimEnd('/');

    private static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(ConsensusOperationTimeout);
        try { return await operation(timeout.Token); }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new TimeoutException("L’opération Raft n’a pas été confirmée avant l’expiration du délai.", exception);
        }
    }

    private static async Task<T> WithMembershipTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(MembershipOperationTimeout);
        try { return await operation(timeout.Token); }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new TimeoutException("La modification de membership Raft n’a pas été confirmée avant l’expiration du délai.", exception);
        }
    }
}
