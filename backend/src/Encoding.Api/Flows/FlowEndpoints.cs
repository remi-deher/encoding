using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.PeerCluster;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowEndpoints
{
    public static void MapFlowEndpoints(this WebApplication app)
    {
        var flows = app.MapGroup("/api/v1/flows").WithTags("Flows");
        flows.MapGet("/{id:guid}/edit", async (Guid id, FlowRepository repository, CancellationToken ct) =>
        {
            if (await repository.DraftAsync(id, ct) is { } draft) return (IResult)Results.Ok(draft);
            return await repository.GetAsync(id, null, ct) is { } saved ? Results.Ok(saved) : ApiProblem.NotFound();
        }).WithName("GetFlowForEdit");

        flows.MapGet("/{id:guid}/preflight", async (Guid id, FlowRepository repository, FlowDefinitionValidator validator,
            Encoding.Api.Executions.RuntimeEligibility eligibility, Encoding.Api.Nodes.NodeRepository nodes,
            Encoding.Api.Nodes.NodePresenceOptions presence, CancellationToken ct) =>
        {
            var draft = await repository.DraftAsync(id, ct);
            var flow = draft?.Definition ?? (await repository.GetAsync(id, null, ct))?.Definition;
            if (flow is null) return ApiProblem.NotFound("Flow introuvable.");
            var validation = validator.Validate(flow);
            var availableWorkers = (await nodes.ListAsync(presence.OfflineAfter, ct))
                .Where(node => node.Status == Encoding.Contracts.Nodes.NodePresenceStatus.Online
                    && node.Lifecycle == Encoding.Contracts.Nodes.NodeLifecycle.Online
                    && node.Roles.Contains(Encoding.Contracts.Nodes.NodeRole.Worker))
                .ToArray();
            var compatible = validation.IsValid
                ? availableWorkers.Where(node => eligibility.CanRun(flow, node.Capabilities, node.Id,
                    identity: new Encoding.Contracts.Nodes.NodeIdentityResponse(node.Id, node.DisplayName,
                        node.Labels ?? new Dictionary<string, string?>(), node.Labels ?? new Dictionary<string, string?>(), []), inventory: node.Inventory)).ToArray()
                : [];
            return Results.Ok(new
            {
                flowId = flow.Id,
                draftRevision = draft?.DraftRevision,
                publishedVersion = draft?.PublishedVersion ?? flow.Version,
                graph = new { valid = validation.IsValid, issues = validation.Issues },
                simulation = new { available = true, status = "not-run", requiredInput = "Un fichier de la bibliothèque cible est nécessaire." },
                execution = new
                {
                    workerCompatible = compatible.Length > 0,
                    status = !validation.IsValid ? "invalid-graph" : compatible.Length > 0 ? "worker-compatible" : "no-compatible-worker",
                    checks = new
                    {
                        workerToolsAndEncoders = "observed",
                        libraryAccess = "requires-target-library",
                        writePermissions = "not-evaluated",
                        aiProvider = "not-evaluated",
                    },
                    compatibleWorkers = compatible.Select(node => new { node.Id, node.Name, node.Capabilities, node.Inventory }),
                    observedAt = DateTimeOffset.UtcNow,
                },
            });
        }).AllowViewer().WithName("PreflightFlowExecution");

        flows.MapPut("/{id:guid}/draft", async (Guid id, SaveFlowDraftRequest request, FlowRepository repository, FlowDefinitionValidator validator, CancellationToken ct) =>
        {
            var validation = validator.ValidateDraft(request.Definition);
            if (!validation.IsValid) return (IResult)Results.UnprocessableEntity(validation);
            return await repository.SaveDraftAsync(request, ct) is { } saved
                ? Results.Ok(saved)
                : ApiProblem.Conflict("Le brouillon a été modifié ailleurs.");
        }).AddEndpointFilter<ValidateFlowDraftFilter>().WithName("SaveFlowDraft");

        flows.MapGet("/{id:guid}/export", async (Guid id, FlowRepository repository, CancellationToken ct) =>
        {
            if (await repository.DraftAsync(id, ct) is { } draft)
                return (IResult)Results.File(JsonSerializer.SerializeToUtf8Bytes(new { kind = "draft", draftRevision = draft.DraftRevision, definition = draft.Definition }, FlowRepository.JsonOptions), "application/json", $"{id:D}.draft.json");
            return await repository.GetAsync(id, null, ct) is { } saved
                ? Results.File(JsonSerializer.SerializeToUtf8Bytes(saved.Definition, FlowRepository.JsonOptions), "application/json", $"{id:D}.v{saved.Definition.Version}.json")
                : ApiProblem.NotFound();
        }).WithName("ExportSingleFlow");

        flows.MapGet("/{id:guid}/usage", async (Guid id, FlowRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.UsageAsync(id, ct))).WithName("GetFlowLibraryUsage");
        flows.MapGet("/{id:guid}/draft-backups", async (Guid id, FlowRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.DraftBackupsAsync(id, ct))).WithName("ListFlowDraftBackups");
        flows.MapGet("/{id:guid}/draft-backups/{revision:int}", async (Guid id, int revision, FlowRepository repository, CancellationToken ct) =>
            await repository.DraftBackupAsync(id, revision, ct) is { } saved ? (IResult)Results.Ok(saved) : ApiProblem.NotFound())
            .WithName("GetFlowDraftBackup");

        flows.MapGet("/{id:guid}/versions", async (Guid id, FlowRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.VersionsAsync(id, ct)))
            .WithName("ListFlowVersions");

        flows.MapPost("/export", async (Guid[] ids, FlowArchiveService archives, CancellationToken ct) =>
        {
            try
            {
                var bytes = await archives.ExportAsync(ids, ct);
                return bytes is null
                    ? ApiProblem.BadRequest("Sélection invalide ou flow introuvable.")
                    : Results.File(bytes, "application/zip", "flows.zip");
            }
            catch (InvalidDataException e) { return ApiProblem.BadRequest(e.Message); }
        }).AllowViewer().RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy).WithName("ExportFlowsArchive");

        flows.MapPost("/import", async (HttpRequest request, FlowArchiveService archives, PeerClusterOptions peerOptions, CancellationToken ct) =>
        {
            try
            {
                var (result, conflict, count) = await archives.ImportAsync(request.Body, request.ContentType == "application/zip", ct);
                if (!result.IsValid) return (IResult)Results.UnprocessableEntity(result);
                return conflict
                    ? ApiProblem.Conflict("Une version existante a un contenu différent. Aucun flow n’a été importé.")
                    : Results.Ok(new { imported = count });
            }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            {
                return ApiProblem.BadRequest("Fichier JSON ou ZIP invalide ou trop volumineux.");
            }
        }).RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy).WithName("ImportFlowsArchive");

        flows.MapGet("", async (FlowRepository repository, CancellationToken ct) => Results.Ok(await repository.ListAsync(ct)))
            .WithName("ListFlows");

        flows.MapGet("/{id:guid}", async (Guid id, FlowRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, null, ct) is { } flow ? (IResult)Results.Ok(flow) : ApiProblem.NotFound())
            .WithName("GetFlow");

        flows.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version, FlowRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, version, ct) is { } flow ? (IResult)Results.Ok(flow) : ApiProblem.NotFound())
            .WithName("GetFlowVersion");

        flows.MapPut("/{id:guid}", async (Guid id, SaveFlowRequest request, FlowDefinitionValidator validator, FlowRepository repository, CancellationToken ct) =>
        {
            var (_, validation) = validator.ValidateDocument(JsonSerializer.SerializeToElement(request.Definition, FlowRepository.JsonOptions));
            if (!validation.IsValid) return (IResult)Results.UnprocessableEntity(validation);
            if (await repository.SaveAsync(request, ct) is not { } saved)
                return ApiProblem.Conflict("Une autre version a été enregistrée. Rechargez le flow avant de publier vos modifications.");
            return Results.Ok(saved);
        }).AddEndpointFilter<ValidateSaveFlowFilter>().WithName("PublishFlowVersion");

        flows.MapDelete("/{id:guid}", async (Guid id, FlowRepository repository, CancellationToken ct) =>
        {
            var (result, reason) = await repository.DeleteFlowAsync(id, ct);
            return result switch
            {
                FlowDeleteResult.Deleted => Results.NoContent(),
                FlowDeleteResult.NotFound => ApiProblem.NotFound("Ce flow est introuvable."),
                FlowDeleteResult.Conflict => ApiProblem.Conflict(reason ?? "Impossible de supprimer ce flow."),
                _ => ApiProblem.NotFound()
            };
        }).WithName("DeleteFlow");

        flows.MapDelete("/{id:guid}/draft", async (Guid id, FlowRepository repository, CancellationToken ct) =>
            await repository.DeleteDraftAsync(id, ct) ? Results.NoContent() : ApiProblem.NotFound("Aucun brouillon pour ce flow."))
            .WithName("DeleteFlowDraft");

        flows.MapPost("/{id:guid}/clone", async (Guid id, CloneFlowRequest? request, FlowRepository repository, CancellationToken ct) =>
            await repository.CloneFlowAsync(id, request?.Name, ct) is { } cloned
                ? Results.Created($"/api/v1/flows/{cloned.Definition.Id}", cloned)
                : ApiProblem.NotFound("Le flow source est introuvable."))
            .WithName("CloneFlow");

        flows.MapPost("/from-template/{templateId:guid}", async (Guid templateId, CreateFlowFromTemplateRequest? request, FlowRepository repository, FlowTemplateRepository templates, CancellationToken ct) =>
            await repository.CreateFromTemplateAsync(templateId, request?.Name, templates, ct) is { } created
                ? Results.Created($"/api/v1/flows/{created.Definition.Id}", created)
                : ApiProblem.NotFound("Le modèle est introuvable."))
            .WithName("CreateFlowFromTemplate");

        flows.MapGet("/components", (FlowComponentRegistry registry) => Results.Ok(registry.Plugins))
            .WithName("GetFlowComponentCatalog");

        flows.MapGet("/schema", (FlowComponentRegistry registry) =>
        {
            var components = registry.Plugins.SelectMany(p => p.Components.Select(c => new
            {
                pluginId = p.Id,
                pluginName = p.Name,
                pluginVersion = p.Version,
                componentId = c.Id,
                componentName = c.Name,
                description = c.Description,
                kind = c.Kind,
                mediaType = c.MediaType,
                supportedFlowMediaTypes = c.SupportedFlowMediaTypes,
                outputs = c.Outputs.Select(o => new { id = o.Id, label = o.Label, description = o.Description }),
                configurationFields = c.ConfigurationSchema.Fields.Select(f => new
                {
                    id = f.Id,
                    label = f.Label,
                    type = f.Type,
                    description = f.Description,
                    required = f.Required,
                    defaultValue = f.DefaultValue,
                    options = f.Options?.Select(opt => new { value = opt.Value, label = opt.Label })
                })
            })).ToArray();

            var example = new
            {
                name = "Exemple Encodage HEVC",
                mediaType = "video",
                publish = true,
                nodes = new object[]
                {
                    new
                    {
                        id = "entry",
                        name = "Entrée vidéo",
                        component = "base-input-video",
                        configuration = new Dictionary<string, object>()
                    },
                    new
                    {
                        id = "audio_rules",
                        name = "Uniformisation audio",
                        component = "base-audio-rules",
                        configuration = new Dictionary<string, object>()
                    }
                },
                connections = new object[]
                {
                    new
                    {
                        id = "c1",
                        sourceNodeId = "entry",
                        sourceOutputId = "success",
                        targetNodeId = "audio_rules"
                    }
                }
            };

            return Results.Ok(new
            {
                formatVersion = 1,
                supportedMediaTypes = new[] { "video", "image", "music", "subtitles" },
                availableComponents = components,
                example
            });
        }).AllowViewer().WithName("GetFlowSchema");

        flows.MapPost("", async (
            JsonElement document,
            bool? publish,
            FlowDefinitionValidator validator,
            FlowRepository repository,
            FlowComponentRegistry registry,
            CancellationToken ct) =>
        {
            var defElement = document;
            if (document.TryGetProperty("definition", out var nested) && nested.ValueKind == JsonValueKind.Object)
                defElement = nested;

            var definition = ParseFlowDefinition(defElement, registry);
            var shouldPublish = publish ?? (document.TryGetProperty("publish", out var pubProp) && pubProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? pubProp.GetBoolean()
                : !(document.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True));

            if (shouldPublish)
            {
                var validation = validator.Validate(definition);
                if (!validation.IsValid)
                    return (IResult)Results.UnprocessableEntity(validation);

                var saved = await repository.SaveAsync(new SaveFlowRequest(0, definition), ct);
                return saved is not null
                    ? Results.Created($"/api/v1/flows/{definition.Id}", saved)
                    : ApiProblem.Conflict("Un flow avec cet identifiant ou cette version existe déjà.");
            }
            else
            {
                var validation = validator.ValidateDraft(definition);
                if (!validation.IsValid)
                    return (IResult)Results.UnprocessableEntity(validation);

                var draft = await repository.SaveDraftAsync(new SaveFlowDraftRequest(0, definition), ct);
                return draft is not null
                    ? Results.Created($"/api/v1/flows/{definition.Id}/edit", draft)
                    : ApiProblem.Conflict("Impossible d'enregistrer le brouillon du flow.");
            }
        }).WithName("CreateFlow");

        flows.MapPost("/validate", (JsonElement document, FlowDefinitionValidator validator) =>
        {
            var (_, result) = validator.ValidateDocument(document);
            return result.IsValid ? (IResult)Results.Ok(result) : Results.UnprocessableEntity(result);
        }).AllowViewer().WithName("ValidateFlowDefinition");
    }

    internal static FlowDefinition ParseFlowDefinition(JsonElement element, FlowComponentRegistry registry)
    {
        if (element.TryGetProperty("definition", out var defProp) && defProp.ValueKind == JsonValueKind.Object)
            element = defProp;

        var id = element.TryGetProperty("id", out var idProp) && idProp.TryGetGuid(out var parsedId) && parsedId != Guid.Empty
            ? parsedId
            : Guid.NewGuid();

        var formatVersion = element.TryGetProperty("formatVersion", out var fvProp) && fvProp.TryGetInt32(out var fv) && fv > 0
            ? fv
            : 1;

        var version = element.TryGetProperty("version", out var vProp) && vProp.TryGetInt32(out var v) && v > 0
            ? v
            : 1;

        var name = element.TryGetProperty("name", out var nProp) && nProp.GetString() is { Length: > 0 } n
            ? n.Trim()
            : "Nouveau Flow";

        var mediaType = (element.TryGetProperty("mediaType", out var mtProp) && mtProp.GetString() is { Length: > 0 } mt
            ? mt
            : (element.TryGetProperty("type", out var tProp) && tProp.GetString() is { Length: > 0 } t ? t : "video")).Trim().ToLowerInvariant();

        var nodes = new List<FlowNodeDefinition>();
        if (element.TryGetProperty("nodes", out var nodesProp) && nodesProp.ValueKind == JsonValueKind.Array)
        {
            var idx = 0;
            foreach (var node in nodesProp.EnumerateArray())
            {
                idx++;
                var nodeId = node.TryGetProperty("id", out var nid) && nid.GetString() is { Length: > 0 } nidStr
                    ? nidStr
                    : $"node_{idx}";
                var nodeName = node.TryGetProperty("name", out var nn) ? nn.GetString() : null;

                FlowComponentReference componentRef;
                if (node.TryGetProperty("component", out var comp))
                {
                    if (comp.ValueKind == JsonValueKind.String)
                    {
                        var compId = comp.GetString()!;
                        componentRef = registry.FindReference(compId) ?? new FlowComponentReference("base", "1.0.0", compId, "1.0.0", 1);
                    }
                    else if (comp.ValueKind == JsonValueKind.Object)
                    {
                        if (comp.TryGetProperty("componentId", out var cid) && comp.TryGetProperty("pluginId", out var pid) && comp.TryGetProperty("pluginVersion", out var pv) && comp.TryGetProperty("componentVersion", out var cv) && comp.TryGetProperty("configurationSchemaVersion", out var csv))
                        {
                            componentRef = new FlowComponentReference(pid.GetString()!, pv.GetString()!, cid.GetString()!, cv.GetString()!, csv.GetInt32());
                        }
                        else
                        {
                            var compId = comp.TryGetProperty("componentId", out var cid2) ? cid2.GetString() : (comp.TryGetProperty("id", out var idComp) ? idComp.GetString() : null);
                            var pluginId = comp.TryGetProperty("pluginId", out var pEl) ? pEl.GetString() : null;
                            componentRef = compId is not null ? (registry.FindReference(compId, pluginId) ?? new FlowComponentReference(pluginId ?? "base", "1.0.0", compId, "1.0.0", 1)) : new FlowComponentReference("base", "1.0.0", "unknown", "1.0.0", 1);
                        }
                    }
                    else
                    {
                        componentRef = new FlowComponentReference("base", "1.0.0", "unknown", "1.0.0", 1);
                    }
                }
                else
                {
                    componentRef = new FlowComponentReference("base", "1.0.0", "unknown", "1.0.0", 1);
                }

                var configuration = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var descriptor = registry.Resolve(componentRef);
                if (descriptor is not null)
                {
                    foreach (var field in descriptor.ConfigurationSchema.Fields)
                    {
                        if (field.DefaultValue is { } defVal) configuration[field.Id] = defVal.Clone();
                    }
                }
                if (node.TryGetProperty("configuration", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in cfg.EnumerateObject())
                        configuration[prop.Name] = prop.Value.Clone();
                }

                nodes.Add(new FlowNodeDefinition(nodeId, nodeName, componentRef, configuration));
            }
        }

        var connections = new List<FlowConnectionDefinition>();
        if (element.TryGetProperty("connections", out var connsProp) && connsProp.ValueKind == JsonValueKind.Array)
        {
            var idx = 0;
            foreach (var conn in connsProp.EnumerateArray())
            {
                idx++;
                var connId = conn.TryGetProperty("id", out var cid) && cid.GetString() is { Length: > 0 } cidStr
                    ? cidStr
                    : $"conn_{idx}";
                var source = conn.TryGetProperty("sourceNodeId", out var sn) ? (sn.GetString() ?? "") : "";
                var output = conn.TryGetProperty("sourceOutputId", out var so) ? (so.GetString() ?? "success") : "success";
                var target = conn.TryGetProperty("targetNodeId", out var tn) ? (tn.GetString() ?? "") : "";
                connections.Add(new FlowConnectionDefinition(connId, source, output, target));
            }
        }

        return new FlowDefinition(formatVersion, id, version, name, mediaType, nodes, connections);
    }
}


