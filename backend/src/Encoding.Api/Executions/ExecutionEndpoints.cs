using Encoding.Api.Auth;


namespace Encoding.Api.Executions;

public static class ExecutionEndpoints
{
    public static void MapExecutionEndpoints(this WebApplication app)
    {
        var executions = app.MapGroup("/api/v1/executions").WithTags("Executions");
        executions.MapGet("", async (ExecutionRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.OverviewAsync(ct))).WithName("GetExecutionOverview");
        executions.MapGet("/{id:guid}", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
        {
            var execution = await repository.GetAsync(id, ct);
            return execution is null ? (IResult)Results.NotFound() : Results.Ok(execution);
        }).AllowNodeCredentials().WithName("GetExecution");
        executions.MapGet("/{id:guid}/events", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is null ? (IResult)Results.NotFound() : Results.Ok(await repository.EventsAsync(id, ct))).WithName("GetExecutionEvents");
        executions.MapGet("/{id:guid}/pipeline", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is null ? (IResult)Results.NotFound() : Results.Ok(await repository.PipelineAsync(id, ct))).WithName("GetExecutionPipeline");
        executions.MapPost("",()=>Results.Json(new{title="Lancez les traitements depuis les fichiers détectés de la bibliothèque."},statusCode:410));
        executions.MapPost("/{id:guid}/cancel", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
            await repository.CancelAsync(id, ct) ? Results.NoContent() : Results.Conflict(new { title = "Cette exécution ne peut pas être annulée dans son état actuel." }))
            .WithName("CancelExecution");
        executions.MapPost("/{id:guid}/retry", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
        {
            var retryId = await repository.RetryAsync(id, ct);
            return retryId is null ? (IResult)Results.Conflict(new { title = "Relance impossible ou déjà enregistrée pour cette tentative." })
                : Results.Created($"/api/v1/executions/{retryId}", new { id = retryId });
        }).WithName("RetryExecution");
    }
}
