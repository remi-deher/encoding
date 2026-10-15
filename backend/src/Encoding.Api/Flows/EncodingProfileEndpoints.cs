using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class EncodingProfileEndpoints
{
    public static void MapEncodingProfileEndpoints(this WebApplication app)
    {
        var profiles = app.MapGroup("/api/v1/encoding-profiles").WithTags("Profils d’encodage");
        profiles.MapGet("/encoders", () => Results.Ok(VideoEncoderCatalog.All))
            .WithName("ListVideoEncoders");
        profiles.MapGet("", async (EncodingProfileRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct))).WithName("ListEncodingProfiles");
        profiles.MapPost("", async (CreateEncodingProfileRequest request, EncodingProfileRepository repository, CancellationToken ct) =>
        {
            var errors = Validate(request.Draft);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            try
            {
                var id = Guid.NewGuid();
                var profile = await repository.CreateAsync(id, request.Draft, ct);
                return Results.Created($"/api/v1/encoding-profiles/{id:D}", profile);
            }
            catch (DbException exception) when (Sql.IsUniqueViolation(exception))
            {
                return Results.Conflict(new { title = "Un profil d’encodage porte déjà ce nom." });
            }
        }).WithName("CreateEncodingProfile");
        profiles.MapGet("/{id:guid}", async (Guid id, EncodingProfileRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } profile ? Results.Ok(profile) : Results.NotFound())
            .WithName("GetEncodingProfile");
        profiles.MapPut("/{id:guid}/draft", async (Guid id, SaveEncodingProfileDraftRequest request, EncodingProfileRepository repository, CancellationToken ct) =>
        {
            var errors = Validate(request.Draft);
            if (request.ExpectedRevision < 1) errors["expectedRevision"] = ["Révision de brouillon invalide."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            if (id == BuiltInEncodingProfiles.BalancedId)
                return Results.Conflict(new { title = "Le profil intégré est en lecture seule. Créez un profil personnalisé à partir de ses valeurs." });
            try
            {
                var saved = await repository.SaveDraftAsync(id, request, ct);
                if (saved is not null) return Results.Ok(saved);
                return await repository.GetAsync(id, ct) is null
                    ? Results.NotFound()
                    : Results.Conflict(new { title = "Le profil a été modifié ailleurs. Rechargez-le avant de continuer." });
            }
            catch (DbException exception) when (Sql.IsUniqueViolation(exception))
            {
                return Results.Conflict(new { title = "Un profil d’encodage porte déjà ce nom." });
            }
        }).WithName("SaveEncodingProfileDraft");
        profiles.MapPost("/{id:guid}/publish", async (Guid id, PublishEncodingProfileRequest request, EncodingProfileRepository repository, CancellationToken ct) =>
        {
            if (request.ExpectedRevision < 1) return Results.BadRequest(new { title = "Révision de brouillon invalide." });
            if (id == BuiltInEncodingProfiles.BalancedId)
                return Results.Conflict(new { title = "Le profil intégré est déjà publié et ne peut pas être modifié." });
            var current = await repository.GetAsync(id, ct);
            if (current is null) return Results.NotFound();
            if (current.Profile.DraftRevision != request.ExpectedRevision)
                return Results.Conflict(new { title = "Le profil a été modifié ailleurs. Rechargez-le avant de publier." });
            var published = await repository.PublishAsync(id, request.ExpectedRevision, ct);
            return published is null
                ? Results.Conflict(new { title = "Le brouillon a changé pendant la publication." })
                : Results.Created($"/api/v1/encoding-profiles/{id:D}/versions/{published.Version}", published);
        }).WithName("PublishEncodingProfile");
        profiles.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version, EncodingProfileRepository repository, CancellationToken ct) =>
            version < 1 ? Results.BadRequest() : await repository.GetVersionAsync(id, version, ct) is { } published
                ? Results.Ok(published)
                : Results.NotFound()).WithName("GetEncodingProfileVersion");
    }

    private static Dictionary<string, string[]> Validate(EncodingProfileDraft? draft)
    {
        var errors = new Dictionary<string, string[]>();
        if (draft is null) return new() { ["draft"] = ["Le brouillon du profil est obligatoire."] };
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 128)
            errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (draft.Description is null || draft.Description.Length > 1000)
            errors["description"] = ["La description est limitée à 1 000 caractères."];
        var targetError = VideoEncoderCatalog.ValidateTargets(draft.Targets);
        if (targetError is not null) errors["targets"] = [targetError];
        return errors;
    }
}
