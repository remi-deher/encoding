using Encoding.Contracts.Libraries;
using Npgsql;

namespace Encoding.Api.Libraries;

public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var libraries = app.MapGroup("/api/v1/libraries").WithTags("Libraries");
        libraries.MapGet("", async (LibraryRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(cancellationToken)))
            .WithName("ListLibraries");
        libraries.MapPost("", async (CreateLibraryRequest request, LibraryRepository repository, CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name)) errors["name"] = ["Le nom de la bibliothèque est obligatoire."];
            else if (request.Name.Trim().Length > 128) errors["name"] = ["Le nom ne peut pas dépasser 128 caractères."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            var created = await repository.CreateAsync(request, cancellationToken);
            return created is null
                ? Results.Conflict(new { title = "Une bibliothèque porte déjà ce nom." })
                : Results.Created($"/api/v1/libraries/{created.Id}", created);
        }).WithName("CreateLibrary");

        libraries.MapPut("/{libraryId:guid}", async (
            Guid libraryId,
            UpdateLibraryRequest request,
            LibraryRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name)) errors["name"] = ["Le nom de la bibliothèque est obligatoire."];
            else if (request.Name.Trim().Length > 128) errors["name"] = ["Le nom ne peut pas dépasser 128 caractères."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            try
            {
                var updated = await repository.UpdateAsync(libraryId, request, cancellationToken);
                return updated is null ? (IResult)Results.NotFound() : Results.Ok(updated);
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                return Results.Conflict(new { title = "Une bibliothèque porte déjà ce nom." });
            }
        }).WithName("UpdateLibrary");

        libraries.MapPut("/{libraryId:guid}/activation", async (
            Guid libraryId,
            SetLibraryActivationRequest request,
            LibraryRepository repository,
            CancellationToken cancellationToken) =>
        {
            var updated = await repository.SetActiveAsync(libraryId, request.IsActive, cancellationToken);
            return updated is null ? (IResult)Results.NotFound() : Results.Ok(updated);
        }).WithName("SetLibraryActivation");

        libraries.MapDelete("/{libraryId:guid}", async (
            Guid libraryId,
            LibraryRepository repository,
            CancellationToken cancellationToken) =>
            await repository.DeleteAsync(libraryId, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteLibrary");

        libraries.MapGet("/{libraryId:guid}/node-access", async (
            Guid libraryId,
            LibraryNodeAccessRepository repository,
            CancellationToken cancellationToken) =>
        {
            var accesses = await repository.ListAsync(libraryId, cancellationToken);
            return accesses is null ? (IResult)Results.NotFound() : Results.Ok(accesses);
        }).WithName("ListLibraryNodeAccesses");

        libraries.MapPut("/{libraryId:guid}/node-access", async (
            Guid libraryId,
            ConfigureLibraryNodeAccessRequest request,
            LibraryNodeAccessRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var accesses = await repository.ConfigureAsync(libraryId, request, cancellationToken);
                return accesses is null ? (IResult)Results.NotFound() : Results.Ok(accesses);
            }
            catch (LibraryNodeAccessValidationException exception)
            {
                return Results.ValidationProblem(exception.Errors.ToDictionary(pair => pair.Key, pair => pair.Value));
            }
        }).WithName("ConfigureLibraryNodeAccesses");

        libraries.MapPut("/{libraryId:guid}/node-access/{nodeId:guid}/activation", async (
            Guid libraryId,
            Guid nodeId,
            SetLibraryNodeAccessActivationRequest request,
            LibraryNodeAccessRepository repository,
            CancellationToken cancellationToken) =>
        {
            var access = await repository.SetEnabledAsync(libraryId, nodeId, request.IsEnabled, cancellationToken);
            return access is null ? (IResult)Results.NotFound() : Results.Ok(access);
        }).WithName("SetLibraryNodeAccessActivation");
    }
}
