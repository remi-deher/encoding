using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Libraries;
using Encoding.Api.Logging;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Logging;
using Encoding.Contracts.Plugins;
using Encoding.Core.Logging;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Mcp;

/// <summary>
/// Service central du serveur Model Context Protocol (MCP).
/// Exécute les requêtes JSON-RPC et expose l'ensemble des outils de gestion du cluster Encoding.
/// </summary>
public sealed class McpService(
    LibraryRepository libraries,
    LibraryWorkflowRepository libraryWorkflows,
    LibraryNodeAccessRepository libraryNodeAccess,
    LibraryProcessingRepository libraryProcessing,
    FlowRepository flows,
    FlowComponentRegistry components,
    FlowDefinitionValidator validator,
    NodeRepository nodes,
    NodeGroupRepository nodeGroups,
    NodePresenceOptions presenceOptions,
    ExecutionRepository executions,
    FlowScriptRepository scripts,
    EncodingProfileRepository profiles,
    FlowTemplateRepository templates,
    FlowComponentPackRepository componentPacks,
    PluginConfigurationRepository pluginConfigurations,
    PluginConnectionTester pluginTester,
    SystemLogRingBuffer ringBuffer,
    SystemLogRepository systemLogs,
    ILogger<McpService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };



    public async Task<McpJsonRpcResponse?> HandleMessageAsync(McpJsonRpcRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.JsonRpc != "2.0" || string.IsNullOrWhiteSpace(request.Method)
            || request.Id is { } suppliedId && suppliedId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null)
            || request.Params is { } suppliedParams && suppliedParams.ValueKind != JsonValueKind.Object)
            return McpJsonRpcResponse.Failure(null, -32600, "Requête JSON-RPC invalide.");
        if (request.Params is { } boundedParams && boundedParams.GetRawText().Length > 1_048_576)
            return McpJsonRpcResponse.Failure(request.Id, -32600, "Les paramètres JSON-RPC dépassent la taille maximale autorisée.");
        // JSON-RPC notifications never receive a response and must not trigger a tool mutation.
        if (request.Id is null)
            return null;
        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "notifications/initialized" => null,
                "ping" => McpJsonRpcResponse.Success(request.Id, new { }),
                "tools/list" => McpJsonRpcResponse.Success(request.Id, new McpToolsListResult(
                    BusinessOperationCatalog.DescribeTools()
                        .Select(operation => new McpToolDefinition(operation.Name, operation.Description, operation.InputSchema)).ToArray())),
                "tools/call" => await HandleCallToolAsync(request, user, ct),
                _ => McpJsonRpcResponse.Failure(request.Id, -32601, $"Méthode inconnue : {request.Method}")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erreur lors du traitement de la requête MCP {Method}", request.Method);
            return McpJsonRpcResponse.Failure(request.Id, -32603, $"Erreur interne du serveur MCP : {ex.Message}");
        }
    }

    /// <summary>Invokes the assistant's bounded read-only subset through the same handlers MCP exposes.</summary>
    public async Task<AiAssistantToolResult> CallAssistantReadToolAsync(AiAssistantToolCall call, CancellationToken ct)
    {
        var operation = BusinessOperationCatalog.FindTool(call.Name);
        if (operation is null || !operation.ReadOnly || !operation.AvailableToAssistant)
            throw new ArgumentException("Outil de lecture IA inconnu ou paramètres invalides.");
        var arguments = call.Arguments is { ValueKind: JsonValueKind.Object } supplied ? supplied : call.Name switch
        {
            "get_flow" when call.FlowId is { } flowId => JsonSerializer.SerializeToElement(new { id = flowId }, JsonOptions),
            "list_executions" => JsonSerializer.SerializeToElement(new { state = call.State, limit = call.Limit ?? 20 }, JsonOptions),
            _ when call.FlowId is null && call.State is null && call.Limit is null => JsonSerializer.SerializeToElement(new { }, JsonOptions),
            _ => default
        };
        if (!BusinessOperationCatalog.ValidateArguments(call.Name, arguments))
            throw new ArgumentException("Les arguments ne correspondent pas au contrat de l’outil.");
        var result = await ExecuteOperationAsync(call.Name, arguments, isAdmin: false, ct);
        if (result.IsError) throw new InvalidOperationException("L’outil de lecture n’a pas pu obtenir son résultat.");
        var raw = result.Content.FirstOrDefault()?.Text ?? "null";
        using var document = JsonDocument.Parse(raw);
        if (raw.Length > 80_000) throw new ArgumentException("Le résultat de l’outil dépasse la limite de contexte.");
        var href = operation.Domain switch
        {
            "libraries" => "/libraries",
            "nodes-and-groups" => "/nodes",
            "executions-and-logs" => "/tasks",
            "scripts" => "/scripts",
            "encoding-profiles" => "/settings/encoding-profiles",
            "plugins-and-integrations" => "/plugins",
            "flow-templates" or "flows-and-components" => "/flows",
            _ => "/"
        };
        return new(call.Name, operation.Description, href, document.RootElement.Clone());
    }

    private static McpJsonRpcResponse HandleInitialize(McpJsonRpcRequest request)
    {
        var result = new McpInitializeResult(
            ProtocolVersion: "2024-11-05",
            Capabilities: new McpCapabilities(Tools: new McpToolsCapability(ListChanged: false)),
            ServerInfo: new McpServerInfo("encoding-mcp", "1.0.0")
        );
        return McpJsonRpcResponse.Success(request.Id, result);
    }

    private async Task<McpJsonRpcResponse> HandleCallToolAsync(McpJsonRpcRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.Params is not { } parameters || parameters.ValueKind != JsonValueKind.Object)
            return McpJsonRpcResponse.Failure(request.Id, -32602, "Paramètres invalides pour tools/call");

        if (!parameters.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String
            || nameProp.GetString() is not { Length: > 0 } toolName)
            return McpJsonRpcResponse.Failure(request.Id, -32602, "Le nom de l'outil est requis dans tools/call");

        if (parameters.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind != JsonValueKind.Object)
            return McpJsonRpcResponse.Failure(request.Id, -32602, "Les arguments de tools/call doivent être un objet.");
        var arguments = parameters.TryGetProperty("arguments", out argsProp)
            ? argsProp
            : JsonSerializer.SerializeToElement(new { }, JsonOptions);

        var callResult = await ExecuteForPrincipalAsync(toolName, arguments, user, ct);

        return McpJsonRpcResponse.Success(request.Id, callResult);
    }

    /// <summary>
    /// Exécute une opération au nom d’un appelant : applique les portées d’un jeton API et journalise toute tentative d’écriture
    /// (outil, acteur, issue et noms des arguments, jamais leurs valeurs).
    /// </summary>
    public async Task<McpCallToolResult> ExecuteForPrincipalAsync(string toolName, JsonElement arguments, ClaimsPrincipal user, CancellationToken ct)
    {
        var operation = BusinessOperationCatalog.FindTool(toolName);
        if (operation is null || operation.ReadOnly)
            return await ExecuteOperationAsync(toolName, arguments, user.IsInRole("admin"), ct);

        var actor = $"{(McpAccess.IsApiToken(user) ? "jeton" : "session")} « {user.Identity?.Name ?? "inconnu"} »";
        var names = arguments.ValueKind == JsonValueKind.Object ? string.Join(',', arguments.EnumerateObject().Select(property => property.Name)) : "";
        if (McpAccess.MissingScope(user, operation) is { } missing)
        {
            logger.LogWarning("Audit MCP : écriture refusée pour {Actor}, outil {Tool}, portée manquante {Scope}.", actor, toolName, missing);
            return McpCallToolResult.Text(McpAccess.MissingScopeMessage(missing), isError: true);
        }

        var result = await ExecuteOperationAsync(toolName, arguments, user.IsInRole("admin"), ct);
        logger.LogWarning("Audit MCP : écriture {Outcome} par {Actor}, outil {Tool}, arguments [{Arguments}].",
            result.IsError ? "en échec ou refusée" : "réussie", actor, toolName, names);
        return result;
    }

    /// <summary>Execute a catalogued operation; all public surfaces share this dispatcher.</summary>
    public Task<McpCallToolResult> ExecuteOperationAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        var operation = BusinessOperationCatalog.FindTool(toolName);
        if (operation is null)
            return Task.FromResult(McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true));
        if (arguments.ValueKind != JsonValueKind.Object)
            arguments = JsonSerializer.SerializeToElement(new { }, JsonOptions);
        if (!BusinessOperationCatalog.ValidateArguments(toolName, arguments))
            return Task.FromResult(McpCallToolResult.Text("Les arguments ne correspondent pas au schéma de l’opération.", isError: true));

        return ExecuteOperationCoreAsync(toolName, arguments, isAdmin, ct);
    }

    private async Task<McpCallToolResult> ExecuteOperationCoreAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        return toolName switch
        {
            // Bibliothèques, nœuds et exécutions
            "get_libraries_overview" => await ToolGetLibrariesOverviewAsync(ct),
            "list_libraries" => await ToolListLibrariesAsync(ct),
            "create_library" => await ToolCreateLibraryAsync(arguments, isAdmin, ct),
            "update_library" => await ToolUpdateLibraryAsync(arguments, isAdmin, ct),
            "delete_library" => await ToolDeleteLibraryAsync(arguments, isAdmin, ct),
            "get_library_node_access" => await ToolGetLibraryNodeAccessAsync(arguments, ct),
            "get_library_workflow" => await ToolGetLibraryWorkflowAsync(arguments, ct),
            "get_library_processing" => await ToolGetLibraryProcessingAsync(arguments, ct),
            "save_library_processing" => await ToolSaveLibraryProcessingAsync(arguments, isAdmin, ct),
            "configure_library_node_access" => await ToolConfigureLibraryNodeAccessAsync(arguments, isAdmin, ct),
            "set_library_activation" => await ToolSetLibraryActivationAsync(arguments, isAdmin, ct),
            "assign_library_flow" => await ToolAssignLibraryFlowAsync(arguments, isAdmin, ct),
            "request_library_discovery" => await ToolRequestLibraryDiscoveryAsync(arguments, isAdmin, ct),
            "list_nodes" => await ToolListNodesAsync(ct),
            "list_node_groups" => await ToolListNodeGroupsAsync(ct),
            "get_node_group" => await ToolGetNodeGroupAsync(arguments, ct),
            "save_node_group" => await ToolSaveNodeGroupAsync(arguments, isAdmin, ct),
            "delete_node_group" => await ToolDeleteNodeGroupAsync(arguments, isAdmin, ct),
            "get_node_activity" => await ToolGetNodeActivityAsync(arguments, ct),
            "get_node_identity" => await ToolGetNodeIdentityAsync(arguments, ct),
            "save_node_identity" => await ToolSaveNodeIdentityAsync(arguments, isAdmin, ct),
            "set_node_draining" => await ToolSetNodeDrainingAsync(arguments, isAdmin, ct),
            "get_node_processing" => await ToolGetNodeProcessingAsync(arguments, ct),
            "save_node_processing" => await ToolSaveNodeProcessingAsync(arguments, isAdmin, ct),
            "list_executions" => await ToolListExecutionsAsync(arguments, ct),
            "get_execution" => await ToolGetExecutionAsync(arguments, ct),
            "get_execution_events" => await ToolGetExecutionEventsAsync(arguments, ct),
            "get_execution_pipeline" => await ToolGetExecutionPipelineAsync(arguments, ct),
            "cancel_execution" => await ToolCancelExecutionAsync(arguments, isAdmin, ct),
            "retry_execution" => await ToolRetryExecutionAsync(arguments, isAdmin, ct),
            "resume_execution" => await ToolResumeExecutionAsync(arguments, isAdmin, ct),
            "get_system_logs" => await ToolGetSystemLogsAsync(arguments, ct),

            // Flows
            "get_flow_schema" => ToolGetFlowSchema(),
            "list_flows" => await ToolListFlowsAsync(ct),
            "get_flow" => await ToolGetFlowAsync(arguments, ct),
            "create_flow" => await ToolCreateFlowAsync(arguments, isAdmin, ct),
            "clone_flow" => await ToolCloneFlowAsync(arguments, isAdmin, ct),
            "delete_flow" => await ToolDeleteFlowAsync(arguments, isAdmin, ct),

            // Scripts
            "list_scripts" => await ToolListScriptsAsync(ct),
            "get_script" => await ToolGetScriptAsync(arguments, ct),
            "save_script" => await ToolSaveScriptAsync(arguments, isAdmin, ct),
            "publish_script" => await ToolPublishScriptAsync(arguments, isAdmin, ct),
            "validate_script_draft" => ToolValidateScriptDraft(arguments),

            // Profils d'encodage
            "list_encoding_profiles" => await ToolListEncodingProfilesAsync(ct),
            "get_encoding_profile" => await ToolGetEncodingProfileAsync(arguments, ct),
            "save_encoding_profile" => await ToolSaveEncodingProfileAsync(arguments, isAdmin, ct),
            "publish_encoding_profile" => await ToolPublishEncodingProfileAsync(arguments, isAdmin, ct),

            // Modèles
            "list_templates" => await ToolListTemplatesAsync(ct),
            "get_template" => await ToolGetTemplateAsync(arguments, ct),
            "create_template_from_flow" => await ToolCreateTemplateFromFlowAsync(arguments, isAdmin, ct),

            // Packs de composants
            "list_component_packs" => await ToolListComponentPacksAsync(ct),
            "get_component_pack" => await ToolGetComponentPackAsync(arguments, ct),
            "create_component_pack_from_flow" => await ToolCreateComponentPackFromFlowAsync(arguments, isAdmin, ct),

            // Plugins
            "list_plugins" => ToolListPlugins(),
            "get_plugin_configuration" => await ToolGetPluginConfigurationAsync(arguments, ct),
            "save_plugin_configuration" => await ToolSavePluginConfigurationAsync(arguments, isAdmin, ct),
            "test_plugin_connection" => await ToolTestPluginConnectionAsync(arguments, ct),
            "get_flow_plugin_configuration" => await ToolGetFlowPluginConfigurationAsync(arguments, ct),
            "save_flow_plugin_configuration" => await ToolSaveFlowPluginConfigurationAsync(arguments, isAdmin, ct),

            // Outils média & validation IA
            "list_media_tools" => ToolListMediaTools(),
            "get_media_tool_info" => ToolGetMediaToolInfo(arguments),
            "validate_tool_definition" => ToolValidateToolDefinition(arguments),
            "validate_plugin_manifest" => ToolValidatePluginManifest(arguments),

            _ => McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true)
        };
    }

    // --- IMPLÉMENTATIONS DES OUTILS ---

    private async Task<McpCallToolResult> ToolGetLibrariesOverviewAsync(CancellationToken ct)
    {
        var overview = await libraries.OverviewAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(overview, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolListLibrariesAsync(CancellationToken ct) =>
        McpCallToolResult.Text(JsonSerializer.Serialize(await libraries.ListAsync(ct), JsonOptions));

    private async Task<McpCallToolResult> ToolCreateLibraryAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La création d’une bibliothèque exige le rôle administrateur.");
        if (!args.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Trim().Length > 128)
            return Invalid("Le nom de bibliothèque est obligatoire et limité à 128 caractères.");
        var created = await libraries.CreateAsync(new(name.GetString()!.Trim()), ct);
        return created is null ? Invalid("Une bibliothèque porte déjà ce nom.") : McpCallToolResult.Text(JsonSerializer.Serialize(created, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolUpdateLibraryAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification d’une bibliothèque exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !args.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Trim().Length > 128)
            return Invalid("Paramètres 'libraryId' et 'name' invalides.");
        try
        {
            var updated = await libraries.UpdateAsync(id, new(name.GetString()!.Trim()), ct);
            return updated is null ? Invalid("Bibliothèque introuvable ou nom déjà utilisé.") : McpCallToolResult.Text(JsonSerializer.Serialize(updated, JsonOptions));
        }
        catch (System.Data.Common.DbException exception) when (Encoding.Api.Infrastructure.Data.Sql.IsUniqueViolation(exception))
        {
            return Invalid("Une bibliothèque porte déjà ce nom.");
        }
    }

    private async Task<McpCallToolResult> ToolDeleteLibraryAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La suppression d’une bibliothèque exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        return await libraries.DeleteAsync(id, ct) ? McpCallToolResult.Text("Définition de bibliothèque supprimée; les fichiers média ne sont pas touchés.") : Invalid("Bibliothèque introuvable.");
    }

    private async Task<McpCallToolResult> ToolGetLibraryNodeAccessAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        var access = await libraryNodeAccess.ListAsync(id, ct);
        return access is null ? Invalid("Bibliothèque introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(access, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetLibraryWorkflowAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        if (!(await libraries.ListAsync(ct)).Any(library => library.Id == id)) return Invalid("Bibliothèque introuvable.");
        var search = args.TryGetProperty("search", out var searchValue) && searchValue.ValueKind == JsonValueKind.String ? searchValue.GetString() : null;
        var limit = args.TryGetProperty("limit", out var limitValue) && limitValue.TryGetInt32(out var l) ? Math.Clamp(l, 1, 10000) : 1000;
        var offset = args.TryGetProperty("offset", out var offsetValue) && offsetValue.TryGetInt32(out var o) ? Math.Max(0, o) : 0;
        var result = await libraryWorkflows.GetAsync(id, search, limit, offset, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetLibraryProcessingAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        if (!(await libraries.ListAsync(ct)).Any(library => library.Id == id)) return Invalid("Bibliothèque introuvable.");
        return McpCallToolResult.Text(JsonSerializer.Serialize(await libraryProcessing.GetAsync(id, ct), JsonOptions));
    }

    private async Task<McpCallToolResult> ToolSaveLibraryProcessingAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La configuration du traitement d’une bibliothèque exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !TryDeserialize<Encoding.Contracts.Libraries.SaveLibraryProcessingSettings>(args, out var request))
            return Invalid("Paramètres de surveillance de bibliothèque invalides.");
        try
        {
            var saved = await libraryProcessing.SaveAsync(id, request, ct);
            return saved ? McpCallToolResult.Text(JsonSerializer.Serialize(await libraryProcessing.GetAsync(id, ct), JsonOptions)) : Invalid("La bibliothèque ou sa révision de configuration a changé.");
        }
        catch (ArgumentException exception) { return Invalid(exception.Message); }
    }

    private async Task<McpCallToolResult> ToolConfigureLibraryNodeAccessAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La configuration des accès exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !TryDeserialize<Encoding.Contracts.Libraries.ConfigureLibraryNodeAccessRequest>(args, out var request))
            return Invalid("Paramètres 'libraryId' et 'nodes' invalides.");
        try
        {
            var result = await libraryNodeAccess.ConfigureAsync(id, request, ct);
            return result is null ? Invalid("Bibliothèque introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (LibraryNodeAccessValidationException exception)
        {
            return Invalid(JsonSerializer.Serialize(exception.Errors, JsonOptions));
        }
    }

    private async Task<McpCallToolResult> ToolSetLibraryActivationAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification d’une bibliothèque exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !args.TryGetProperty("isActive", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Invalid("Paramètres 'libraryId' et 'isActive' requis.");
        var result = await libraries.SetActiveAsync(id, enabled.GetBoolean(), ct);
        return result is null ? Invalid("Bibliothèque introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolAssignLibraryFlowAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("L’affectation d’un flow exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !TryInt(args, "expectedRevision", out var revision) || revision < 0)
            return Invalid("Paramètres 'libraryId' et 'expectedRevision' invalides.");
        Guid? flowId = null;
        int? version = null;
        if (args.TryGetProperty("flowId", out var flowValue) && flowValue.ValueKind != JsonValueKind.Null)
        {
            if (!Guid.TryParse(flowValue.GetString(), out var parsedFlow) || parsedFlow == Guid.Empty || !TryInt(args, "version", out var parsedVersion) || parsedVersion < 1)
                return Invalid("Une affectation exige un 'flowId' et une version publiée valide.");
            flowId = parsedFlow;
            version = parsedVersion;
        }
        else if (args.TryGetProperty("version", out _))
        {
            return Invalid("Le paramètre 'version' nécessite un 'flowId'.");
        }
        var updated = await libraryWorkflows.AssignAsync(id, new(flowId, version, revision), ct);
        return updated ? McpCallToolResult.Text(JsonSerializer.Serialize(await libraryWorkflows.GetAsync(id, ct), JsonOptions)) : Invalid("L’affectation a changé ou la version du flow n’est pas publiable.");
    }

    private async Task<McpCallToolResult> ToolRequestLibraryDiscoveryAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La découverte de fichiers exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        var scan = await libraryWorkflows.RequestScanAsync(id, ct);
        return scan is null ? Invalid("Activez la bibliothèque et affectez-lui un flow publié avant la découverte.") : McpCallToolResult.Text(JsonSerializer.Serialize(new { scanId = scan }, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolListNodeGroupsAsync(CancellationToken ct) =>
        McpCallToolResult.Text(JsonSerializer.Serialize(await nodeGroups.ListAsync(ct), JsonOptions));

    private async Task<McpCallToolResult> ToolGetNodeGroupAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var group = await nodeGroups.GetAsync(id, ct);
        return group is null ? Invalid("Groupe de nœuds introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(group, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolSaveNodeGroupAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification des groupes exige le rôle administrateur.");
        if (!TryDeserialize<Encoding.Contracts.Nodes.SaveNodeGroupRequest>(args, out var request)) return Invalid("Définition de groupe invalide.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128
            || request.Description is null || request.Description.Length > 2000
            || request.Criteria is null || request.Labels is null || request.NodeIds is null || request.NodeIds.Count > 2048
            || request.Labels.Count > 128 || !Enum.IsDefined(request.Mode)
            || request.Mode == Encoding.Contracts.Nodes.NodeGroupMode.Automatic && request.NodeIds.Count > 0)
            return Invalid("Les champs du groupe ou ses limites sont invalides.");
        Guid? id = null;
        if (args.TryGetProperty("id", out var idValue))
        {
            if (idValue.ValueKind != JsonValueKind.String || !Guid.TryParse(idValue.GetString(), out var parsed) || parsed == Guid.Empty)
                return Invalid("Le paramètre 'id' doit être un UUID valide.");
            id = parsed;
        }
        try
        {
            var saved = await nodeGroups.SaveAsync(id, request, ct);
            if (!saved) return Invalid("Groupe introuvable ou paramètres invalides.");
            var group = id is { } groupId
                ? await nodeGroups.GetAsync(groupId, ct)
                : (await nodeGroups.ListAsync(ct)).FirstOrDefault(item => item.Name == request.Name.Trim());
            return McpCallToolResult.Text(JsonSerializer.Serialize(group, JsonOptions));
        }
        catch (System.Data.Common.DbException ex) when (Encoding.Api.Infrastructure.Data.Sql.IsUniqueViolation(ex))
        {
            return Invalid("Un groupe porte déjà ce nom.");
        }
    }

    private async Task<McpCallToolResult> ToolDeleteNodeGroupAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La suppression des groupes exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        return await nodeGroups.DeleteAsync(id, ct) switch
        {
            "deleted" => McpCallToolResult.Text("Groupe supprimé."),
            "in-use" => Invalid("Le groupe est référencé par un flow et ne peut pas être supprimé."),
            _ => Invalid("Groupe de nœuds introuvable.")
        };
    }

    private async Task<McpCallToolResult> ToolGetNodeActivityAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "nodeId", out var id)) return Invalid("Paramètre 'nodeId' (UUID) requis.");
        var activity = await nodes.ActivityAsync(id, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(activity, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetNodeIdentityAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "nodeId", out var id)) return Invalid("Paramètre 'nodeId' (UUID) requis.");
        var identity = await nodeGroups.GetNodeIdentityAsync(id, ct);
        return identity is null ? Invalid("Nœud introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(identity, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolSaveNodeIdentityAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification de l’identité d’un nœud exige le rôle administrateur.");
        if (!TryGuid(args, "nodeId", out var id) || !TryDeserialize<Encoding.Contracts.Nodes.SaveNodeIdentityRequest>(args, out var request))
            return Invalid("Paramètres d’identité de nœud invalides.");
        if (request.Labels is null || request.Labels.Count > 128 || request.DisplayName?.Length > 128
            || request.Labels.Any(pair => !System.Text.RegularExpressions.Regex.IsMatch(pair.Key, "^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$") || pair.Value?.Length > 256))
            return Invalid("Le nom ou les étiquettes du nœud sont invalides.");
        var saved = await nodeGroups.SaveNodeIdentityAsync(id, request, ct);
        return saved ? McpCallToolResult.Text(JsonSerializer.Serialize(await nodeGroups.GetNodeIdentityAsync(id, ct), JsonOptions)) : Invalid("Nœud introuvable.");
    }

    private async Task<McpCallToolResult> ToolSetNodeDrainingAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La vidange d’un nœud exige le rôle administrateur.");
        if (!TryGuid(args, "nodeId", out var id) || !args.TryGetProperty("draining", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid("Paramètres 'nodeId' et 'draining' invalides.");
        return await nodes.SetDrainingAsync(id, value.GetBoolean(), ct) ? McpCallToolResult.Text("État de vidange mis à jour.") : Invalid("Nœud introuvable.");
    }

    private async Task<McpCallToolResult> ToolGetNodeProcessingAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "nodeId", out var id)) return Invalid("Paramètre 'nodeId' (UUID) requis.");
        var settings = await libraryProcessing.NodeAsync(id, ct);
        return settings is null ? Invalid("Nœud introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(settings, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolSaveNodeProcessingAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La configuration de capacité d’un nœud exige le rôle administrateur.");
        if (!TryGuid(args, "nodeId", out var id) || !TryDeserialize<Encoding.Contracts.Nodes.SaveNodeProcessingSettings>(args, out var request))
            return Invalid("Paramètres de capacité du nœud invalides.");
        try
        {
            var saved = await libraryProcessing.SaveNodeAsync(id, request, ct);
            return saved ? McpCallToolResult.Text(JsonSerializer.Serialize(await libraryProcessing.NodeAsync(id, ct), JsonOptions)) : Invalid("Le nœud ou sa révision de configuration a changé.");
        }
        catch (ArgumentException exception) { return Invalid(exception.Message); }
    }

    private async Task<McpCallToolResult> ToolGetExecutionAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var result = await executions.GetAsync(id, ct);
        return result is null ? Invalid("Exécution introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetExecutionEventsAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        if (await executions.GetAsync(id, ct) is null) return Invalid("Exécution introuvable.");
        int? after = TryInt(args, "afterRevision", out var revision) ? Math.Max(0, revision) : null;
        int? limit = TryInt(args, "limit", out var count) ? Math.Clamp(count, 1, 1000) : null;
        return McpCallToolResult.Text(JsonSerializer.Serialize(await executions.EventsAsync(id, after, limit, ct), JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetExecutionPipelineAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        if (await executions.GetAsync(id, ct) is null) return Invalid("Exécution introuvable.");
        return McpCallToolResult.Text(JsonSerializer.Serialize(await executions.PipelineAsync(id, ct), JsonOptions));
    }

    private async Task<McpCallToolResult> ToolCancelExecutionAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("L’annulation d’une exécution exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        int? revision = TryInt(args, "expectedRevision", out var value) ? value : null;
        return await executions.CancelAsync(id, ct, revision) ? McpCallToolResult.Text("Annulation demandée.") : Invalid("Exécution introuvable ou non annulable dans cet état.");
    }

    private async Task<McpCallToolResult> ToolRetryExecutionAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La relance d’une exécution exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var next = await executions.RetryAsync(id, ct);
        return next is null ? Invalid("La relance est réservée aux exécutions en échec et ne peut être enregistrée qu’une fois.") : McpCallToolResult.Text(JsonSerializer.Serialize(new { id = next }, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolResumeExecutionAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La reprise d’une exécution exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var resumedId = await executions.ResumeAsync(id, ct);
        return resumedId is { } next
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { id = next, state = "queued", sourceExecutionId = id }, JsonOptions))
            : Invalid("Reprise impossible : seule une exécution annulée sans reprise précédente peut être relancée.");
    }

    private McpCallToolResult ToolGetFlowSchema()
    {
        var available = components.Plugins.SelectMany(p => p.Components.Select(c => new
        {
            componentId = c.Id,
            pluginId = p.Id,
            name = c.Name,
            description = c.Description,
            category = c.Kind,
            mediaType = c.MediaType,
            supportedFlowMediaTypes = c.SupportedFlowMediaTypes,
            outputs = c.Outputs,
            schema = c.ConfigurationSchema
        })).ToList();

        var example = new
        {
            formatVersion = 1,
            name = "Exemple Flow Automatique",
            mediaType = "video",
            nodes = new object[]
            {
                new { id = "entry", component = "base-input-file", name = "Entrée Fichier" },
                new { id = "audio_rules", component = "base-audio-rules", name = "Règles Audio" }
            },
            connections = new object[]
            {
                new { id = "c1", sourceNodeId = "entry", sourceOutputId = "success", targetNodeId = "audio_rules" }
            }
        };

        var response = new
        {
            formatVersion = 1,
            supportedMediaTypes = new[] { "video", "image", "music", "subtitles" },
            availableComponents = available,
            example
        };

        return McpCallToolResult.Text(JsonSerializer.Serialize(response, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolListFlowsAsync(CancellationToken ct)
    {
        var list = await flows.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetFlowAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID valide) requis.", isError: true);

        if (await flows.DraftAsync(id, ct) is { } draft)
            return McpCallToolResult.Text(JsonSerializer.Serialize(new { isDraft = true, draft }, JsonOptions));

        if (await flows.GetAsync(id, null, ct) is { } flow)
            return McpCallToolResult.Text(JsonSerializer.Serialize(new { isDraft = false, flow }, JsonOptions));

        return McpCallToolResult.Text($"Flow introuvable avec l'ID {id}.", isError: true);
    }

    private async Task<McpCallToolResult> ToolCreateFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la création de flow requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object)
            return McpCallToolResult.Text("Arguments invalides pour create_flow.", isError: true);

        var definition = FlowEndpoints.ParseFlowDefinition(arguments, components);
        var shouldPublish = !arguments.TryGetProperty("publish", out var pubProp) || pubProp.ValueKind != JsonValueKind.False;

        if (shouldPublish)
        {
            var validation = validator.Validate(definition);
            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Issues.Select(e => $"{e.Code}: {e.Message}"));
                return McpCallToolResult.Text($"Validation échouée pour la publication du flow : {errors}", isError: true);
            }

            var saved = await flows.SaveAsync(new SaveFlowRequest(0, definition), ct);
            if (saved is null)
                return McpCallToolResult.Text("Conflit : un flow avec cet identifiant ou cette version existe déjà.", isError: true);

            return McpCallToolResult.Text(JsonSerializer.Serialize(new
            {
                message = "Flow créé et publié avec succès en version 1.",
                flow = saved
            }, JsonOptions));
        }
        else
        {
            var validation = validator.ValidateDraft(definition);
            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Issues.Select(e => $"{e.Code}: {e.Message}"));
                return McpCallToolResult.Text($"Validation échouée pour le brouillon du flow : {errors}", isError: true);
            }

            var draft = await flows.SaveDraftAsync(new SaveFlowDraftRequest(0, definition), ct);
            if (draft is null)
                return McpCallToolResult.Text("Impossible d'enregistrer le brouillon du flow.", isError: true);

            return McpCallToolResult.Text(JsonSerializer.Serialize(new
            {
                message = "Brouillon de flow enregistré avec succès.",
                draft
            }, JsonOptions));
        }
    }

    private async Task<McpCallToolResult> ToolCloneFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : le clonage de flow requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("sourceFlowId", out var srcProp) || !srcProp.TryGetGuid(out var sourceId)
            || !arguments.TryGetProperty("newName", out var nameProp) || nameProp.GetString() is not { Length: > 0 } newName)
        {
            return McpCallToolResult.Text("Paramètres 'sourceFlowId' (UUID) et 'newName' (chaîne) requis.", isError: true);
        }

        var cloned = await flows.CloneFlowAsync(sourceId, newName.Trim(), ct);
        if (cloned is null)
            return McpCallToolResult.Text($"Flow source introuvable avec l'ID {sourceId}.", isError: true);

        return McpCallToolResult.Text(JsonSerializer.Serialize(new
        {
            message = "Flow cloné avec succès.",
            flow = cloned
        }, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolDeleteFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la suppression de flow requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("flowId", out var idProp) || !idProp.TryGetGuid(out var flowId))
        {
            return McpCallToolResult.Text("Paramètre 'flowId' (UUID) requis.", isError: true);
        }

        var (deleteResult, reason) = await flows.DeleteFlowAsync(flowId, ct);
        return deleteResult switch
        {
            FlowDeleteResult.Deleted => McpCallToolResult.Text($"Le flow {flowId} a été supprimé avec succès."),
            FlowDeleteResult.NotFound => McpCallToolResult.Text($"Flow introuvable avec l'ID {flowId}.", isError: true),
            FlowDeleteResult.Conflict => McpCallToolResult.Text($"Impossible de supprimer le flow {flowId} : {reason ?? "conflit"}", isError: true),
            _ => McpCallToolResult.Text("Erreur inconnue lors de la suppression du flow.", isError: true)
        };
    }

    private async Task<McpCallToolResult> ToolListNodesAsync(CancellationToken ct)
    {
        var list = await nodes.ListAsync(presenceOptions.OfflineAfter, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    private McpCallToolResult ToolValidateScriptDraft(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return Invalid("Objet brouillon requis.");
        var errors = new Dictionary<string, string[]>();
        var name = args.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
        var language = args.TryGetProperty("language", out var languageValue) && languageValue.ValueKind == JsonValueKind.String ? languageValue.GetString() : null;
        var description = args.TryGetProperty("description", out var descriptionValue) && descriptionValue.ValueKind == JsonValueKind.String ? descriptionValue.GetString() : null;
        var code = args.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128) errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (language is not ("javascript" or "python")) errors["language"] = ["Choisissez JavaScript ou Python."];
        if (description is null || description.Length > 1000) errors["description"] = ["La description est obligatoire et limitée à 1 000 caractères."];
        if (code is null || code.Length > 200000) errors["code"] = ["Le code est obligatoire et limité à 200 000 caractères."];
        return McpCallToolResult.Text(JsonSerializer.Serialize(new { valid = errors.Count == 0, errors }, JsonOptions));
    }

    private static McpCallToolResult Invalid(string message) => McpCallToolResult.Text(message, isError: true);
    private static McpCallToolResult Denied(string message) => McpCallToolResult.Text("Permission refusée : " + message, isError: true);
    private static bool TryGuid(JsonElement args, string name, out Guid value)
    {
        value = Guid.Empty;
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out value) && value != Guid.Empty;
    }
    private static bool TryInt(JsonElement args, string name, out int value)
    {
        value = 0;
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out value);
    }
    private static bool TryDeserialize<T>(JsonElement args, out T value) where T : class
    {
        try
        {
            value = args.Deserialize<T>(JsonOptions)!;
            return value is not null;
        }
        catch (JsonException)
        {
            value = null!;
            return false;
        }
    }

    private async Task<McpCallToolResult> ToolListExecutionsAsync(JsonElement arguments, CancellationToken ct)
    {
        string? state = null;
        var limit = 50;

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("state", out var stateProp) && stateProp.GetString() is { Length: > 0 } st)
                state = st.Trim();

            if (arguments.TryGetProperty("limit", out var limitProp) && limitProp.TryGetInt32(out var l) && l > 0)
                limit = Math.Clamp(l, 1, 200);
        }

        var filter = new ExecutionFilterQuery(Limit: limit, State: state);
        var overview = await executions.OverviewAsync(filter, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(overview, JsonOptions));
    }

    // --- SCRIPTS ---

    private async Task<McpCallToolResult> ToolListScriptsAsync(CancellationToken ct)
    {
        var list = await scripts.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetScriptAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID) requis.", isError: true);

        var script = await scripts.GetAsync(id, ct);
        return script is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(script, JsonOptions))
            : McpCallToolResult.Text($"Script introuvable avec l'ID {id}.", isError: true);
    }

    private async Task<McpCallToolResult> ToolSaveScriptAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la modification de scripts requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name
            || !arguments.TryGetProperty("language", out var langProp) || langProp.GetString() is not { Length: > 0 } lang
            || !arguments.TryGetProperty("code", out var codeProp) || codeProp.GetString() is not { } code)
        {
            return McpCallToolResult.Text("Paramètres 'name', 'language' (javascript|python) et 'code' requis.", isError: true);
        }

        var desc = arguments.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        var draft = new FlowScriptDraft(name.Trim(), lang.Trim().ToLowerInvariant(), desc.Trim(), code);

        Guid scriptId;
        if (arguments.TryGetProperty("id", out var idProp) && idProp.TryGetGuid(out var existingId))
        {
            scriptId = existingId;
            var existing = await scripts.GetAsync(scriptId, ct);
            if (existing is not null)
            {
                var expectedRev = arguments.TryGetProperty("expectedRevision", out var revProp) && revProp.TryGetInt32(out var r) ? r : existing.Script.DraftRevision;
                var saved = await scripts.SaveDraftAsync(scriptId, new SaveFlowScriptDraftRequest(expectedRev, draft), ct);
                return saved is not null
                    ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Brouillon de script mis à jour.", script = saved }, JsonOptions))
                    : McpCallToolResult.Text("Conflit de révision ou script introuvable.", isError: true);
            }
            else
            {
                var created = await scripts.CreateAsync(scriptId, draft, ct);
                return McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Script créé avec succès.", script = created }, JsonOptions));
            }
        }
        else
        {
            scriptId = Guid.NewGuid();
            var created = await scripts.CreateAsync(scriptId, draft, ct);
            return McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Script créé avec succès.", script = created }, JsonOptions));
        }
    }

    private async Task<McpCallToolResult> ToolPublishScriptAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la publication de scripts requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id)
            || !arguments.TryGetProperty("expectedRevision", out var revProp) || !revProp.TryGetInt32(out var rev))
        {
            return McpCallToolResult.Text("Paramètres 'id' (UUID) et 'expectedRevision' (entier) requis.", isError: true);
        }

        var published = await scripts.PublishAsync(id, rev, ct);
        return published is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = $"Script publié en version {published.Version}.", version = published }, JsonOptions))
            : McpCallToolResult.Text("Échec de publication : script introuvable, code vide ou révision conflictuelle.", isError: true);
    }

    // --- PROFILS D'ENCODAGE ---

    private async Task<McpCallToolResult> ToolListEncodingProfilesAsync(CancellationToken ct)
    {
        var list = await profiles.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetEncodingProfileAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID) requis.", isError: true);

        var profile = await profiles.GetAsync(id, ct);
        return profile is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(profile, JsonOptions))
            : McpCallToolResult.Text($"Profil d'encodage introuvable avec l'ID {id}.", isError: true);
    }

    private async Task<McpCallToolResult> ToolSaveEncodingProfileAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la modification de profils requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name
            || !arguments.TryGetProperty("targets", out var targetsProp) || targetsProp.ValueKind != JsonValueKind.Array)
        {
            return McpCallToolResult.Text("Paramètres 'name' et 'targets' (tableau de cibles d'encodage) requis.", isError: true);
        }

        var desc = arguments.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        var targets = new List<EncodingProfileTarget>();
        foreach (var t in targetsProp.EnumerateArray())
        {
            var encId = t.TryGetProperty("encoderId", out var ep) ? ep.GetString() ?? "" : "";
            var q = t.TryGetProperty("quality", out var qp) && qp.TryGetDecimal(out var qVal) ? qVal : 22m;
            var preset = t.TryGetProperty("preset", out var pp) ? pp.GetString() ?? "medium" : "medium";
            var priority = t.TryGetProperty("priority", out var prioProp) && prioProp.TryGetInt32(out var prio) ? prio : 10;
            targets.Add(new EncodingProfileTarget(encId, q, preset, priority));
        }

        var draft = new EncodingProfileDraft(name.Trim(), desc.Trim(), targets);

        Guid profileId;
        if (arguments.TryGetProperty("id", out var idProp) && idProp.TryGetGuid(out var existingId))
        {
            profileId = existingId;
            var existing = await profiles.GetAsync(profileId, ct);
            if (existing is not null)
            {
                var expectedRev = arguments.TryGetProperty("expectedRevision", out var revProp) && revProp.TryGetInt32(out var r) ? r : existing.Profile.DraftRevision;
                var saved = await profiles.SaveDraftAsync(profileId, new SaveEncodingProfileDraftRequest(expectedRev, draft), ct);
                return saved is not null
                    ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Brouillon de profil d'encodage mis à jour.", profile = saved }, JsonOptions))
                    : McpCallToolResult.Text("Conflit de révision ou profil introuvable / intégré.", isError: true);
            }
            else
            {
                var created = await profiles.CreateAsync(profileId, draft, ct);
                return McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Profil d'encodage créé.", profile = created }, JsonOptions));
            }
        }
        else
        {
            profileId = Guid.NewGuid();
            var created = await profiles.CreateAsync(profileId, draft, ct);
            return McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Profil d'encodage créé.", profile = created }, JsonOptions));
        }
    }

    private async Task<McpCallToolResult> ToolPublishEncodingProfileAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la publication de profils requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id)
            || !arguments.TryGetProperty("expectedRevision", out var revProp) || !revProp.TryGetInt32(out var rev))
        {
            return McpCallToolResult.Text("Paramètres 'id' (UUID) et 'expectedRevision' (entier) requis.", isError: true);
        }

        var published = await profiles.PublishAsync(id, rev, ct);
        return published is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = $"Profil publié en version {published.Version}.", version = published }, JsonOptions))
            : McpCallToolResult.Text("Échec de publication : profil introuvable ou révision conflictuelle.", isError: true);
    }

    // --- MODÈLES (TEMPLATES) ---

    private async Task<McpCallToolResult> ToolListTemplatesAsync(CancellationToken ct)
    {
        var list = await templates.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetTemplateAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID) requis.", isError: true);

        var template = await templates.GetAsync(id, ct);
        return template is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(template, JsonOptions))
            : McpCallToolResult.Text($"Modèle introuvable avec l'ID {id}.", isError: true);
    }

    private async Task<McpCallToolResult> ToolCreateTemplateFromFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la création de modèles requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("sourceFlowId", out var srcProp) || !srcProp.TryGetGuid(out var srcId)
            || !arguments.TryGetProperty("sourceVersion", out var verProp) || !verProp.TryGetInt32(out var ver)
            || !arguments.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name)
        {
            return McpCallToolResult.Text("Paramètres 'sourceFlowId' (UUID), 'sourceVersion' (entier) et 'name' requis.", isError: true);
        }

        var desc = arguments.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        var request = new CreateFlowTemplateFromFlowRequest(srcId, ver, name.Trim(), desc.Trim());
        var created = await templates.CreateFromFlowAsync(Guid.NewGuid(), request, ct);

        return created is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Modèle créé à partir du flow avec succès.", template = created }, JsonOptions))
            : McpCallToolResult.Text("Flow source ou version publiée introuvable.", isError: true);
    }

    // --- PACKS DE COMPOSANTS ---

    private async Task<McpCallToolResult> ToolListComponentPacksAsync(CancellationToken ct)
    {
        var list = await componentPacks.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetComponentPackAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID) requis.", isError: true);

        var pack = await componentPacks.GetAsync(id, ct);
        return pack is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(pack, JsonOptions))
            : McpCallToolResult.Text($"Pack de composants introuvable avec l'ID {id}.", isError: true);
    }

    private async Task<McpCallToolResult> ToolCreateComponentPackFromFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la création de packs requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("sourceFlowId", out var srcProp) || !srcProp.TryGetGuid(out var srcId)
            || !arguments.TryGetProperty("sourceVersion", out var verProp) || !verProp.TryGetInt32(out var ver)
            || !arguments.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name)
        {
            return McpCallToolResult.Text("Paramètres 'sourceFlowId' (UUID), 'sourceVersion' (entier) et 'name' requis.", isError: true);
        }

        var desc = arguments.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        var request = new CreateFlowComponentPackRequest(srcId, ver, name.Trim(), desc.Trim());
        var created = await componentPacks.CreateFromFlowAsync(Guid.NewGuid(), request, ct);

        return created is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Pack de composants créé avec succès.", pack = created }, JsonOptions))
            : McpCallToolResult.Text("Impossible de créer le pack : le flow doit contenir une entrée reliée à au moins un composant.", isError: true);
    }

    // --- PLUGINS ---

    private McpCallToolResult ToolListPlugins()
    {
        var list = components.Plugins.Select(p => new
        {
            p.Id,
            p.Name,
            p.Version,
            p.ConfigurationSchema,
            componentsCount = p.Components.Count,
            components = p.Components.Select(c => new
            {
                c.Id,
                c.Name,
                c.Description,
                c.Kind,
                c.MediaType
            })
        }).ToList();

        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetPluginConfigurationAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("pluginId", out var idProp) || idProp.GetString() is not { Length: > 0 } pluginId)
            return McpCallToolResult.Text("Paramètre 'pluginId' requis.", isError: true);

        var plugin = components.Plugins.SingleOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return McpCallToolResult.Text($"Plugin '{pluginId}' introuvable.", isError: true);

        var config = await pluginConfigurations.GetGlobalAsync(plugin, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(config, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolSavePluginConfigurationAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la configuration des plugins requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("pluginId", out var idProp) || idProp.GetString() is not { Length: > 0 } pluginId
            || !arguments.TryGetProperty("values", out var valsProp) || valsProp.ValueKind != JsonValueKind.Object)
        {
            return McpCallToolResult.Text("Paramètres 'pluginId' et 'values' (objet) requis.", isError: true);
        }

        var plugin = components.Plugins.SingleOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return McpCallToolResult.Text($"Plugin '{pluginId}' introuvable.", isError: true);

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in valsProp.EnumerateObject())
            values[prop.Name] = prop.Value.Clone();

        var clearSecrets = new List<string>();
        if (arguments.TryGetProperty("clearSecrets", out var csProp) && csProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in csProp.EnumerateArray())
                if (item.GetString() is { Length: > 0 } sec) clearSecrets.Add(sec);
        }

        var expectedRev = arguments.TryGetProperty("expectedRevision", out var revProp) && revProp.TryGetInt32(out var r) ? r : 0;
        var request = new SavePluginConfigurationRequest(expectedRev, values, clearSecrets);

        var saved = await pluginConfigurations.SaveGlobalAsync(plugin, request, ct);
        return saved is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Configuration du plugin enregistrée.", configuration = saved }, JsonOptions))
            : McpCallToolResult.Text("Échec d'enregistrement de la configuration.", isError: true);
    }

    private async Task<McpCallToolResult> ToolTestPluginConnectionAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("pluginId", out var idProp) || idProp.GetString() is not { Length: > 0 } pluginId)
            return McpCallToolResult.Text("Paramètre 'pluginId' requis.", isError: true);

        var plugin = components.Plugins.SingleOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return McpCallToolResult.Text($"Plugin '{pluginId}' introuvable.", isError: true);

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("values", out var valsProp) && valsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in valsProp.EnumerateObject())
                values[prop.Name] = prop.Value.Clone();
        }

        var resolvedValues = await pluginConfigurations.ResolveGlobalForConnectionTestAsync(plugin, values, ct);
        var testResult = await pluginTester.TestAsync(plugin.Id, resolvedValues, ct);

        return McpCallToolResult.Text(JsonSerializer.Serialize(testResult, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolGetFlowPluginConfigurationAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "flowId", out var flowId) || !args.TryGetProperty("pluginId", out var pluginValue)
            || pluginValue.ValueKind != JsonValueKind.String) return Invalid("Paramètres 'flowId' et 'pluginId' requis.");
        var plugin = components.Plugins.SingleOrDefault(item => item.Id == pluginValue.GetString());
        if (plugin is null) return Invalid("Plugin introuvable.");
        if (await flows.GetAsync(flowId, null, ct) is null && await flows.DraftAsync(flowId, ct) is null)
            return Invalid("Flow introuvable.");
        var configuration = await pluginConfigurations.GetFlowAsync(flowId, plugin, ct);
        return configuration is null ? Invalid("Flow introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(configuration, JsonOptions));
    }

    private async Task<McpCallToolResult> ToolSaveFlowPluginConfigurationAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La configuration d’un plugin dans un flow exige le rôle administrateur.");
        if (!TryGuid(args, "flowId", out var flowId) || !args.TryGetProperty("pluginId", out var pluginValue)
            || pluginValue.ValueKind != JsonValueKind.String || !TryDeserialize<Encoding.Contracts.Plugins.SavePluginConfigurationRequest>(args, out var request))
            return Invalid("Paramètres de configuration du plugin dans le flow invalides.");
        var plugin = components.Plugins.SingleOrDefault(item => item.Id == pluginValue.GetString());
        if (plugin is null) return Invalid("Plugin introuvable.");
        if (await flows.GetAsync(flowId, null, ct) is null && await flows.DraftAsync(flowId, ct) is null)
            return Invalid("Flow introuvable.");
        try
        {
            var saved = await pluginConfigurations.SaveFlowAsync(flowId, plugin, request, ct);
            return saved is null ? Invalid("La configuration du plugin n’a pas pu être enregistrée.") : McpCallToolResult.Text(JsonSerializer.Serialize(saved, JsonOptions));
        }
        catch (PluginConfigurationValidationException exception) { return Invalid(exception.Message); }
    }

    // --- 8. OUTILS MÉDIA & VALIDATION IA ---

    private McpCallToolResult ToolListMediaTools()
    {
        var tools = new object[]
        {
            new
            {
                id = "ffmpeg",
                name = "FFmpeg",
                description = "Moteur multimédia complet pour l'encodage, le décodage, le transcodage, le filtrage et la normalisation audio/vidéo.",
                executables = new[] { "ffmpeg" },
                mediaTypes = new[] { "video", "audio", "subtitles", "general" },
                commonOperations = new[] { "transcode_video", "transcode_audio", "normalize_audio_ebur128", "extract_track", "change_container", "create_thumbnail", "crop_detect" },
                scriptUsage = "tools.run(\"ffmpeg\", [\"-hide_banner\", \"-i\", \"source.mkv\", ...])",
                notes = "Outil de référence système conservé pour les sondes matérielles (0064). S'exécute sous Landlock sur les fichiers temporaires."
            },
            new
            {
                id = "ffprobe",
                name = "FFprobe",
                description = "Analyseur de flux et de conteneurs pour extraire les métadonnées techniques détaillées.",
                executables = new[] { "ffprobe" },
                mediaTypes = new[] { "video", "audio", "subtitles", "general" },
                commonOperations = new[] { "probe_streams", "probe_format", "read_tags", "detect_codecs" },
                scriptUsage = "tools.run(\"ffprobe\", [\"-v\", \"quiet\", \"-print_format\", \"json\", \"-show_format\", \"-show_streams\", \"file.mkv\"])",
                notes = "Sortie JSON recommandée pour un parsing fiable par les scripts et l'IA."
            },
            new
            {
                id = "mkvtoolnix",
                name = "MKVToolNix",
                description = "Suite d'outils spécialisée pour le format Matroska (MKV) : multiplexage, extraction et modification de propriétés en place.",
                executables = new[] { "mkvmerge", "mkvextract", "mkvpropedit" },
                mediaTypes = new[] { "video", "audio", "subtitles" },
                commonOperations = new[] { "remux_mkv", "extract_subtitles_ass", "extract_audio_raw", "edit_track_names", "set_default_track" },
                scriptUsage = "tools.run(\"mkvmerge\", [\"-o\", \"output.mkv\", \"--no-audio\", \"input.mkv\"])",
                notes = "Attention : mkvmerge renvoie 0 pour succès total et 1 en présence d'avertissements (considéré valide dans Encoding)."
            },
            new
            {
                id = "mediainfo",
                name = "MediaInfo",
                description = "Extracteur d'informations techniques approfondies (profils HDR, Dolby Vision, agencement de canaux audio).",
                executables = new[] { "mediainfo" },
                mediaTypes = new[] { "video", "audio", "general" },
                commonOperations = new[] { "read_hdr_metadata", "read_audio_channels_layout", "export_json" },
                scriptUsage = "tools.run(\"mediainfo\", [\"--Output=JSON\", \"file.mkv\"])",
                notes = "Complément indispensable pour les métadonnées HDR10 / Dolby Vision."
            },
            new
            {
                id = "imagemagick",
                name = "ImageMagick",
                description = "Traitement, conversion et redimensionnement d'images, jaquettes et miniatures.",
                executables = new[] { "magick", "convert" },
                mediaTypes = new[] { "image" },
                commonOperations = new[] { "resize", "crop", "convert_format", "optimize_poster" },
                scriptUsage = "tools.run(\"magick\", [\"input.png\", \"-resize\", \"600x900\", \"output.jpg\"])",
                notes = "Utilisé pour la génération et retouche de visuels."
            },
            new
            {
                id = "python",
                name = "Python 3 Runtime",
                description = "Moteur d'exécution pour les plugins d'intégration et scripts avancés.",
                executables = new[] { "python3", "python" },
                mediaTypes = new[] { "general" },
                commonOperations = new[] { "run_plugin_process", "execute_custom_script" },
                scriptUsage = "Utilise le SDK encoding_plugin_sdk.py pour communiquer avec le worker.",
                notes = "S'exécute sous le bac à sable Landlock (0032). Aucun accès aux secrets du nœud."
            },
            new
            {
                id = "javascript",
                name = "Node.js / JavaScript Runtime",
                description = "Moteur de scripts légers et évaluation d'expressions conditionnelles.",
                executables = new[] { "node" },
                mediaTypes = new[] { "general" },
                commonOperations = new[] { "evaluate_conditions", "lightweight_script" },
                scriptUsage = "base-script avec moteur JavaScript.",
                notes = "Confiné sous Landlock."
            }
        };

        return McpCallToolResult.Text(JsonSerializer.Serialize(tools, JsonOptions));
    }

    private McpCallToolResult ToolGetMediaToolInfo(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("toolId", out var idProp) || idProp.GetString() is not { Length: > 0 } toolId)
            return McpCallToolResult.Text("Paramètre 'toolId' requis.", isError: true);

        var id = toolId.Trim().ToLowerInvariant();

        object? result = id switch
        {
            "ffmpeg" => new
            {
                id = "ffmpeg",
                name = "FFmpeg",
                executables = new[] { "ffmpeg" },
                role = "Transcodage, filtrage et encodage vidéo/audio universel",
                conformanceRequirements = new[]
                {
                    "Option -version renvoyant une version >= 5.1",
                    "Option -hide_banner -encoders renvoyant la liste des encodeurs reconnus",
                    "Smoke test synthétique CPU (1s mire de test encodée sans erreur)"
                },
                safeUsageGuidelines = new[]
                {
                    "Toujours utiliser -hide_banner et -nostats pour éviter de polluer les logs",
                    "Spécifier -y pour écraser la sortie temporaire de travail",
                    "Ne jamais interpoler de chemins sans guillemets ou dans un shell arbitraire",
                    "Consulter VideoEncoderCatalog pour les options de qualité par encodeur"
                },
                supportedVideoEncoders = VideoEncoderCatalog.All.Select(e => new
                {
                    e.Id,
                    e.Codec,
                    e.Name,
                    e.Acceleration,
                    e.QualityMode,
                    e.QualityOption,
                    e.PresetOption,
                    qualityRange = new { min = e.QualityMinimum, max = e.QualityMaximum, @default = e.DefaultQuality },
                    presets = e.Presets.Select(p => new { p.Id, p.Name, p.Value })
                }).ToList()
            },
            "ffprobe" => new
            {
                id = "ffprobe",
                name = "FFprobe",
                executables = new[] { "ffprobe" },
                role = "Analyse et sondage de flux multimédia",
                recommendedCommand = new[] { "ffprobe", "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", "{filePath}" },
                expectedOutputFormat = "JSON structuré contenant les tableaux 'streams' (codec_name, width, height, channels, tags) et 'format' (duration, size, bit_rate)."
            },
            "mkvtoolnix" => new
            {
                id = "mkvtoolnix",
                name = "MKVToolNix",
                executables = new[] { "mkvmerge", "mkvextract", "mkvpropedit" },
                subTools = new[]
                {
                    new { executable = "mkvmerge", purpose = "Remuxer des conteneurs, sélectionner/ordonner des flux et ajouter des pistes.", exitCodeRules = "Code 0 = Succès, Code 1 = Succès avec avertissements mineurs (valide), Code > 1 = Erreur critique." },
                    new { executable = "mkvextract", purpose = "Extraire des pistes de sous-titres (ASS/SRT), pistes audio natives, chapitres ou pièces jointes.", exitCodeRules = "Code 0 = Succès." },
                    new { executable = "mkvpropedit", purpose = "Modifier les métadonnées de pistes (titre, langue, flag 'default', flag 'forced') directement sur la copie de travail sans réécriture intégrale.", exitCodeRules = "Code 0 = Succès, Code 1 = Avertissements." }
                }
            },
            "mediainfo" => new
            {
                id = "mediainfo",
                name = "MediaInfo",
                executables = new[] { "mediainfo" },
                role = "Inspection avancée des métadonnées de diffusion et mastering",
                recommendedCommand = new[] { "mediainfo", "--Output=JSON", "{filePath}" },
                keyFields = new[] { "MasteringDisplay_ColorPrimaries", "MasteringDisplay_Luminance", "MaxCLL", "MaxFALL", "HDR_Format", "Format_Commercial_IfAny" }
            },
            "imagemagick" => new
            {
                id = "imagemagick",
                name = "ImageMagick",
                executables = new[] { "magick", "convert" },
                role = "Conversion et retouche graphique d'affiches/miniatures",
                typicalCommands = new[]
                {
                    "magick input.jpg -resize 300x450^ -gravity center -extent 300x450 poster.jpg",
                    "magick input.png -strip -quality 85 output.webp"
                }
            },
            _ => null
        };

        return result is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions))
            : McpCallToolResult.Text($"Outil '{toolId}' non répertorié ou aucune documentation spécifique disponible.", isError: true);
    }

    private McpCallToolResult ToolValidateToolDefinition(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("definition", out var defProp) || defProp.ValueKind != JsonValueKind.Object)
            return McpCallToolResult.Text("Paramètre 'definition' (objet JSON décrivant tool.json) requis.", isError: true);

        var errors = new List<string>();
        var warnings = new List<string>();

        // 1. Id
        if (!defProp.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
            errors.Add("Le champ 'id' est obligatoire.");
        else if (!Regex.IsMatch(id, "^[a-z0-9_-]+$"))
            errors.Add($"L'identifiant '{id}' doit être en minuscules et ne contenir que des lettres, chiffres, tirets et underscores.");

        // 2. Name
        if (!defProp.TryGetProperty("name", out var nameProp) || string.IsNullOrWhiteSpace(nameProp.GetString()))
            errors.Add("Le champ 'name' est obligatoire.");

        // 3. Executable
        if (!defProp.TryGetProperty("executable", out var execProp) || string.IsNullOrWhiteSpace(execProp.GetString()))
            errors.Add("Le champ 'executable' est obligatoire.");

        // 4. Probe
        if (!defProp.TryGetProperty("probe", out var probeProp) || probeProp.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Le champ 'probe' est obligatoire (objet décrivant la commande de version et la regex).");
        }
        else
        {
            if (!probeProp.TryGetProperty("versionArgs", out var vArgs) || vArgs.ValueKind != JsonValueKind.Array || vArgs.GetArrayLength() == 0)
                errors.Add("Le champ 'probe.versionArgs' doit être un tableau non vide de chaînes (ex: ['--version']).");

            if (!probeProp.TryGetProperty("versionRegex", out var vRegex) || vRegex.GetString() is not { Length: > 0 } regexStr)
            {
                errors.Add("Le champ 'probe.versionRegex' est obligatoire.");
            }
            else
            {
                try
                {
                    _ = new Regex(regexStr);
                }
                catch (Exception ex)
                {
                    errors.Add($"L'expression régulière 'probe.versionRegex' est invalide : {ex.Message}");
                }
            }
        }

        // 5. Capabilities
        if (!defProp.TryGetProperty("capabilitiesExposed", out var capProp) || capProp.ValueKind != JsonValueKind.Array || capProp.GetArrayLength() == 0)
            warnings.Add("Le champ 'capabilitiesExposed' est vide ou absent. L'outil n'annoncera aucune capacité au cluster.");

        var isValid = errors.Count == 0;
        var response = new
        {
            isValid,
            errors,
            warnings,
            summary = isValid ? "Manifeste d'outil conforme aux spécifications de l'architecture 0064." : "Des erreurs bloquantes empêchent l'enregistrement de l'outil."
        };

        return McpCallToolResult.Text(JsonSerializer.Serialize(response, JsonOptions), isError: !isValid);
    }

    private McpCallToolResult ToolValidatePluginManifest(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("manifest", out var manProp) || manProp.ValueKind != JsonValueKind.Object)
            return McpCallToolResult.Text("Paramètre 'manifest' (objet JSON décrivant plugin.json) requis.", isError: true);

        var errors = new List<string>();
        var warnings = new List<string>();
        var suggestions = new List<string>();

        // 1. Id avec Namespace strict (0063)
        if (!manProp.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
        {
            errors.Add("Le champ 'id' est obligatoire.");
        }
        else
        {
            if (!id.Contains('.'))
                errors.Add($"L'identifiant '{id}' doit respecter la règle de namespace strict '<namespace>.<plugin-name>' (ex: 'community.whisper').");
            if (id.StartsWith("official.", StringComparison.OrdinalIgnoreCase))
                warnings.Add("Le namespace 'official.' est réservé aux plugins du dépôt officiel Encoding.");
        }

        // 2. Version SemVer
        if (!manProp.TryGetProperty("version", out var verProp) || verProp.GetString() is not { Length: > 0 } ver)
            errors.Add("Le champ 'version' est obligatoire.");
        else if (!Regex.IsMatch(ver, @"^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$"))
            errors.Add($"La version '{ver}' n'est pas un SemVer valide (ex: 1.0.0 ou 1.2.3-beta).");

        // 3. Runtime Python sous Landlock (0032)
        if (manProp.TryGetProperty("runtime", out var rtProp) && rtProp.ValueKind == JsonValueKind.Object)
        {
            if (rtProp.TryGetProperty("type", out var typeProp) && typeProp.GetString() != "python3")
                errors.Add("Le runtime du plugin doit être 'python3' pour s'exécuter sous le bac à sable Landlock (les exécutables .NET ou compilés arbitraires sont proscrits, architecture 0032).");
            if (!rtProp.TryGetProperty("entrypoint", out var epProp) || string.IsNullOrWhiteSpace(epProp.GetString()))
                errors.Add("Le champ 'runtime.entrypoint' (ex: 'main.py') est obligatoire.");
        }

        // 4. Composants et sémantique IA (0062)
        if (!manProp.TryGetProperty("components", out var compsProp) || compsProp.ValueKind != JsonValueKind.Array || compsProp.GetArrayLength() == 0)
        {
            warnings.Add("Le plugin ne déclare aucun composant de flow dans 'components'.");
        }
        else
        {
            var compIndex = 0;
            foreach (var comp in compsProp.EnumerateArray())
            {
                compIndex++;
                var compId = comp.TryGetProperty("id", out var cId) ? cId.GetString() : $"composant_{compIndex}";

                if (!comp.TryGetProperty("description", out var cDesc) || (cDesc.GetString() ?? "").Length < 10)
                    suggestions.Add($"Composant '{compId}' : Fournir une description détaillée (> 10 caractères) pour aider l'IA à comprendre le rôle du bloc.");

                if (comp.TryGetProperty("outputs", out var outsProp) && outsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var outItem in outsProp.EnumerateArray())
                    {
                        var outId = outItem.TryGetProperty("id", out var oId) ? oId.GetString() : "?";
                        if (!outItem.TryGetProperty("description", out var oDesc) || string.IsNullOrWhiteSpace(oDesc.GetString()))
                            errors.Add($"Composant '{compId}', Sortie '{outId}' : La description sémantique est OBLIGATOIRE (règle 0062) pour éviter que l'IA n'inverse les branches de succès et d'échec.");
                    }
                }
                else
                {
                    errors.Add($"Composant '{compId}' : Doit déclarer un tableau 'outputs' avec au moins une sortie.");
                }
            }
        }

        var isValid = errors.Count == 0;
        var result = new
        {
            isValid,
            errors,
            warnings,
            suggestions,
            summary = isValid ? "Manifeste de plugin validé avec succès pour l'écosystème IA." : "Le manifeste comporte des erreurs de conformité bloquantes."
        };

        return McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions), isError: !isValid);
    }

    private async Task<McpCallToolResult> ToolGetSystemLogsAsync(JsonElement arguments, CancellationToken ct)
    {
        var minLevelStr = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("min_level", out var mlProp)
            ? mlProp.GetString()
            : "Warning";
        var minLevel = Enum.TryParse<SystemLogLevel>(minLevelStr, true, out var parsedLevel)
            ? parsedLevel
            : SystemLogLevel.Warning;

        var category = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("category", out var catProp)
            ? catProp.GetString()
            : null;

        var search = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("search", out var sProp)
            ? sProp.GetString()
            : null;

        var limit = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var l)
            ? Math.Clamp(l, 1, 200)
            : 50;

        Guid? nodeId = null;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("node_id", out var nProp) &&
            Guid.TryParse(nProp.GetString(), out var parsedNodeId))
        {
            nodeId = parsedNodeId;
        }

        var filter = new SystemLogFilterQuery(minLevel, category, search, nodeId, limit);
        var inMemory = ringBuffer.Query(filter);

        if (filter.MinLevel >= SystemLogLevel.Warning || inMemory.Count < filter.Limit)
        {
            var dbEntries = await systemLogs.QueryAsync(filter, ct);
            if (dbEntries.Count > 0)
            {
                var seenIds = new HashSet<string>(inMemory.Select(e => e.Id));
                var combined = new List<SystemLogEntry>(inMemory);
                foreach (var dbEntry in dbEntries)
                {
                    if (seenIds.Add(dbEntry.Id))
                    {
                        combined.Add(dbEntry);
                    }
                }
                combined.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                return McpCallToolResult.Text(JsonSerializer.Serialize(combined.Take(filter.Limit).ToList(), JsonOptions));
            }
        }

        return McpCallToolResult.Text(JsonSerializer.Serialize(inMemory, JsonOptions));
    }
}
