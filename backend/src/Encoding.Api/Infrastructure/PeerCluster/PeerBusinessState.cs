using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerBusinessStateDocument(long Revision, bool Deleted, JsonElement Value);

/// <summary>The library catalog fields shared by peers. Per-node path grants remain local projections.</summary>
public sealed record PeerLibraryState(Guid Id, string Name, DateTimeOffset CreatedAt, bool IsActive);
public sealed record PeerFlowVersionState(FlowDefinition Definition, DateTimeOffset CreatedAt, int? ExpectedDraftRevision = null);
public sealed record PeerFlowDraftState(Guid FlowId, int Revision, int PublishedVersion, FlowDefinition? Definition,
    DateTimeOffset UpdatedAt, bool HasDraft);
public sealed record PeerLibraryWorkflowState(Guid LibraryId, Guid? FlowId, int? FlowVersion, int Revision);

public static class PeerBusinessState
{
    public const string LibraryAggregate = "library";
    private const string Prefix = "business/library/";
    private const string FlowVersionPrefix = "business/flow-version/";
    private const string FlowDraftPrefix = "business/flow-draft/";
    private const string LibraryWorkflowPrefix = "business/library-workflow/";

    public static string LibraryKey(Guid id) => $"{Prefix}{id:D}";
    public static string FlowVersionKey(Guid id, int version) => $"{FlowVersionPrefix}{id:D}/{version:D10}";
    public static string FlowDraftKey(Guid id) => $"{FlowDraftPrefix}{id:D}";
    public static string LibraryWorkflowKey(Guid id) => $"{LibraryWorkflowPrefix}{id:D}";

    public static bool TryReadLibraryKey(string key, out Guid id)
    {
        if (key.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key.AsSpan(Prefix.Length), "D", out id)) return true;
        id = Guid.Empty;
        return false;
    }

    public static bool TryReadFlowVersionKey(string key, out Guid id, out int version)
    {
        id = Guid.Empty;
        version = 0;
        if (!key.StartsWith(FlowVersionPrefix, StringComparison.Ordinal)) return false;
        var parts = key.AsSpan(FlowVersionPrefix.Length);
        var separator = parts.IndexOf('/');
        return separator > 0
            && Guid.TryParseExact(parts[..separator], "D", out id)
            && int.TryParse(parts[(separator + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out version)
            && version > 0;
    }

    public static bool TryReadFlowDraftKey(string key, out Guid id)
    {
        if (key.StartsWith(FlowDraftPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key.AsSpan(FlowDraftPrefix.Length), "D", out id)) return true;
        id = Guid.Empty;
        return false;
    }

    public static bool TryReadLibraryWorkflowKey(string key, out Guid id)
    {
        if (key.StartsWith(LibraryWorkflowPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key.AsSpan(LibraryWorkflowPrefix.Length), "D", out id)) return true;
        id = Guid.Empty;
        return false;
    }

    public static bool TryParseLibraryDocument(JsonElement value, out PeerBusinessStateDocument document)
    {
        try
        {
            document = value.Deserialize<PeerBusinessStateDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return document is { Revision: > 0 } && (document.Deleted || document.Value.ValueKind == JsonValueKind.Object);
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    public static bool TryParseFlowVersion(JsonElement value, out PeerFlowVersionState state)
    {
        try
        {
            state = value.Deserialize<PeerFlowVersionState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return state is not null && state.Definition is { Version: > 0 } && state.Definition.Id != Guid.Empty;
        }
        catch (JsonException)
        {
            state = null!;
            return false;
        }
    }

    public static bool TryParseFlowDraft(JsonElement value, out PeerFlowDraftState state)
    {
        try
        {
            state = value.Deserialize<PeerFlowDraftState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return state is not null && state.Revision > 0 && (state.HasDraft
                ? state.Definition is not null && state.Definition.Id == state.FlowId
                : state.Definition is null || state.Definition.Id == state.FlowId);
        }
        catch (JsonException)
        {
            state = null!;
            return false;
        }
    }

    public static bool TryParseLibraryWorkflow(JsonElement value, out PeerLibraryWorkflowState state)
    {
        try
        {
            state = value.Deserialize<PeerLibraryWorkflowState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return state is not null && state.LibraryId != Guid.Empty && state.Revision > 0
                && (state.FlowId is null) == (state.FlowVersion is null)
                && (state.FlowId is not null ? state.FlowVersion > 0 : state.FlowVersion is null);
        }
        catch (JsonException)
        {
            state = null!;
            return false;
        }
    }
}
