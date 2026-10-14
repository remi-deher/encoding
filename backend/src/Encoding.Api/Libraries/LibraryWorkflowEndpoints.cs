
using Encoding.Contracts.Libraries;
namespace Encoding.Api.Libraries;

public static class LibraryWorkflowEndpoints
{
    public static void MapLibraryWorkflowEndpoints(this WebApplication app)
    {
        var libraries=app.MapGroup("/api/v1/libraries/{library:guid}");
        libraries.MapGet("/processing",async(Guid library,LibraryProcessingRepository repository,CancellationToken ct)=>Results.Ok(await repository.GetAsync(library,ct)));
        libraries.MapPut("/processing",async(Guid library,SaveLibraryProcessingSettings request,LibraryProcessingRepository repository,CancellationToken ct)=>
        {
            try{return await repository.SaveAsync(library,request,ct)?(IResult)Results.NoContent():Results.Conflict(new{title="La configuration a changé. Actualisez la bibliothèque."});}
            catch(ArgumentException e){return Results.BadRequest(new{title=e.Message});}
        });
        libraries.MapGet("/workflow",async(Guid library,LibraryWorkflowRepository repository,CancellationToken ct)=>Results.Ok(await repository.GetAsync(library,ct)));
        libraries.MapPut("/flow",async(Guid library,AssignLibraryFlowRequest request,LibraryWorkflowRepository repository,CancellationToken ct)=>
            await repository.AssignAsync(library,request,ct)?(IResult)Results.NoContent():Results.Conflict(new{title="Version publiée invalide ou configuration modifiée ailleurs."}));
        libraries.MapPost("/discover",async(Guid library,LibraryWorkflowRepository repository,CancellationToken ct)=>
            await repository.RequestScanAsync(library,ct) is {} id?(IResult)Results.Accepted(value:new{id}):Results.Conflict(new{title="Activez la bibliothèque et attribuez-lui un flow publié avant la découverte."}));
        libraries.MapPost("/executions",async(Guid library,EnqueueLibraryFilesRequest request,LibraryWorkflowRepository repository,CancellationToken ct)=>
            request.FileIds is not null && await repository.EnqueueAsync(library,request.FileIds,request.AssignmentRevision,ct) is {} result
                ?(IResult)Results.Ok(new{queued=result.Queued,skipped=result.Skipped}):Results.Conflict(new{title="La sélection ou le flow attribué n’est plus disponible. Actualisez la bibliothèque."}));
    }
}
