using System.Text.Json;
using Encoding.Api.Auth;

namespace Encoding.Api.Infrastructure;

public sealed record ClusterReconciliationExchangeRequest(
    int ProtocolVersion,
    Guid ClusterId,
    IReadOnlyDictionary<Guid, long> KnownSequences,
    IReadOnlyList<ClusterChangeEvent> Changes);

public sealed record ClusterReconciliationExchangeResponse(
    int ProtocolVersion,
    Guid ClusterId,
    IReadOnlyDictionary<Guid, long> KnownSequences,
    IReadOnlyList<ClusterChangeEvent> Changes);

public static class ClusterReconciliationEndpoints
{
    public const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapClusterReconciliationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/cluster/reconciliation/exchange", async (
            HttpContext http,
            IConfiguration configuration,
            ClusterIdentityRepository identityRepository,
            ClusterReconciliationRepository repository,
            ClusterEntityReconciliation entityReconciliation,
            CancellationToken ct) =>
        {
            var configuredSecret = configuration["Cluster:Reconciliation:SharedSecret"];
            if (string.IsNullOrWhiteSpace(configuredSecret) || System.Text.Encoding.UTF8.GetByteCount(configuredSecret) < 32)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "La réconciliation de cluster n’est pas configurée.");
            var body = await ReadBoundedBodyAsync(http.Request, ct);
            if (body is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!ClusterReconciliationSignature.Verify(
                    configuredSecret,
                    http.Request.Headers[ClusterReconciliationSignature.TimeHeader],
                    http.Request.Headers[ClusterReconciliationSignature.SignatureHeader],
                    body)) return Results.Unauthorized();
            ClusterReconciliationExchangeRequest? request;
            try { request = JsonSerializer.Deserialize<ClusterReconciliationExchangeRequest>(body, JsonOptions); }
            catch (JsonException) { return Results.BadRequest(new { title = "Requête de réconciliation invalide." }); }
            if (request is null || request.KnownSequences is null || request.Changes is null)
                return Results.BadRequest(new { title = "Requête de réconciliation incomplète." });
            if (request.ProtocolVersion != ProtocolVersion) return Results.Conflict(new { title = "Version de protocole de réconciliation incompatible." });
            if (request.KnownSequences.Count > 4096 || request.KnownSequences.Any(entry => entry.Key == Guid.Empty || entry.Value < 0))
                return Results.BadRequest(new { title = "Vecteur de réconciliation invalide." });
            if (request.Changes.Count > ClusterReconciliationRepository.MaximumBatchSize)
                return Results.BadRequest(new { title = "Lot de réconciliation trop volumineux." });

            var identity = await identityRepository.GetAsync(ct);
            if (request.ClusterId != identity.Id) return Results.Conflict(new { title = "Les serveurs n’appartiennent pas au même cluster." });
            try
            {
                await repository.ApplyIncomingAsync(request.Changes, ct);
                await entityReconciliation.ApplyPendingAsync(ct);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { title = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { title = exception.Message }); }
            catch (JsonException) { return Results.BadRequest(new { title = "Événement de réconciliation mal formé." }); }

            var vector = await repository.GetVectorAsync(ct);
            var changes = (await repository.GetEventsAfterAsync(identity.Id, request.KnownSequences, ClusterReconciliationRepository.MaximumBatchSize, ct)).ToList();
            var responseBody = SerializeBoundedResponse(identity.Id, vector, changes);
            var responseTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers[ClusterReconciliationSignature.TimeHeader] = responseTime;
            http.Response.Headers[ClusterReconciliationSignature.SignatureHeader] = ClusterReconciliationSignature.Create(configuredSecret, responseTime, responseBody);
            return Results.Bytes(responseBody, "application/json");
        })
        .AllowAnonymous()
        .RequireRateLimiting(AuthExtensions.ClusterMeshPolicy)
        .WithTags("Cluster reconciliation")
        .WithName("ExchangeClusterReconciliationEvents");
    }

    private static byte[] SerializeBoundedResponse(Guid clusterId, IReadOnlyDictionary<Guid, long> vector, List<ClusterChangeEvent> changes)
    {
        byte[] body;
        do
        {
            body = JsonSerializer.SerializeToUtf8Bytes(new ClusterReconciliationExchangeResponse(ProtocolVersion, clusterId, vector, changes), JsonOptions);
            if (body.Length <= ClusterReconciliationSignature.MaximumBodyBytes) return body;
            if (changes.Count == 0) throw new InvalidOperationException("Le vecteur de réconciliation dépasse la taille maximale autorisée.");
            changes.RemoveAt(changes.Count - 1);
        } while (true);
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > ClusterReconciliationSignature.MaximumBodyBytes) return null;
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) return output.ToArray();
            if (output.Length + read > ClusterReconciliationSignature.MaximumBodyBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
