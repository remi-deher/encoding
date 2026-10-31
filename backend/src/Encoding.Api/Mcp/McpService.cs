using System.Security.Claims;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Encoding.Api.Logging;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Api.Resources;
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
public sealed partial class McpService(
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
    ResourceRepository resources,
    EncodingProfileRepository profiles,
    FlowTemplateRepository templates,
    FlowComponentPackRepository componentPacks,
    PluginConfigurationRepository pluginConfigurations,
    PluginConnectionTester pluginTester,
    SystemLogRingBuffer ringBuffer,
    SystemLogRepository systemLogs,
    ILogger<McpService> logger,
    Encoding.Api.Updates.ReleaseService releases)
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
            "get_version" => McpCallToolResult.Text(JsonSerializer.Serialize(new { version = releases.Installed.Version, commit = releases.Installed.Commit }, JsonOptions)),
            "get_updates" => McpCallToolResult.Text(JsonSerializer.Serialize(await releases.GetUpdatesAsync(ct), JsonOptions)),
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
            "delete_script" => await ToolDeleteScriptAsync(arguments, isAdmin, ct),
            "validate_script_draft" => ToolValidateScriptDraft(arguments),

            // Ressources partagées
            "list_resources" => await ToolListResourcesAsync(ct),
            "get_resource" => await ToolGetResourceAsync(arguments, ct),
            "create_resource" => await ToolCreateResourceAsync(arguments, isAdmin, ct),
            "update_resource" => await ToolUpdateResourceAsync(arguments, isAdmin, ct),
            "add_resource_version" => await ToolAddResourceVersionAsync(arguments, isAdmin, ct),
            "delete_resource" => await ToolDeleteResourceAsync(arguments, isAdmin, ct),

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
}
