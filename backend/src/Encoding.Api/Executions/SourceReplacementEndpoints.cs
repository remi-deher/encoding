using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

public static class SourceReplacementEndpoints
{
    public static void MapSourceReplacementEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/libraries/{library:guid}/replacement-policy",async(Guid library,SourceReplacementRepository repository,CancellationToken ct)=>
            await repository.GetPolicyAsync(library,ct) is {} policy?(IResult)Results.Ok(policy):Results.NotFound());
        app.MapPut("/api/v1/libraries/{library:guid}/replacement-policy",async(Guid library,SaveLibraryReplacementPolicy request,SourceReplacementRepository repository,CancellationToken ct)=>
        {
            try{return await repository.SavePolicyAsync(library,request,ct)?(IResult)Results.NoContent():Results.Conflict(new{title="La politique a changé ou la bibliothèque est introuvable. Actualisez la bibliothèque."});}
            catch(ArgumentException e){return Results.BadRequest(new{title=e.Message});}
        });
        app.MapGet("/api/v1/executions/{id:guid}/replacement",async(Guid id,SourceReplacementRepository repository,CancellationToken ct)=>
            await repository.GetAsync(id,ct) is {} replacement?(IResult)Results.Ok(replacement):Results.NotFound());
        app.MapPost("/api/v1/executions/{id:guid}/replacement/rollback",async(Guid id,SourceReplacementRepository repository,CancellationToken ct)=>
            await repository.RequestRollbackAsync(id,ct) switch
            {
                "ok"=>Results.Accepted(),
                "missing"=>Results.NotFound(),
                "busy"=>Results.Conflict(new{title="Ce média est en cours de traitement ; réessayez plus tard."}),
                _=>Results.Conflict(new{title="La restauration n’est plus possible : la période de grâce est terminée ou le remplacement n’est pas actif."})
            });
    }
}
