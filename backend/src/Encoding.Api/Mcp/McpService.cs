using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Libraries;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Mcp;

/// <summary>
/// Service central du serveur Model Context Protocol (MCP).
/// Exécute les requêtes JSON-RPC et expose l'ensemble des outils de gestion du cluster Encoding.
/// </summary>
public sealed class McpService(
    LibraryRepository libraries,
    FlowRepository flows,
    FlowComponentRegistry components,
    FlowDefinitionValidator validator,
    NodeRepository nodes,
    NodePresenceOptions presenceOptions,
    ExecutionRepository executions,
    FlowScriptRepository scripts,
    EncodingProfileRepository profiles,
    FlowTemplateRepository templates,
    FlowComponentPackRepository componentPacks,
    PluginConfigurationRepository pluginConfigurations,
    PluginConnectionTester pluginTester,
    ILogger<McpService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyList<McpToolDefinition> DefinedTools =
    [
        // --- 1. BIBLIOTHÈQUES & SANTÉ DU CLUSTER ---
        new(
            "get_libraries_overview",
            "Obtient une vue d'ensemble complète et consolidée de toutes les bibliothèques multimédias du cluster (flows associés, modes d'accès par nœud, état des scans et compteurs de fichiers).",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "list_nodes",
            "Liste tous les nœuds du cluster, leurs rôles (serveur, worker), état de présence (en ligne/hors ligne) et capacités d'encodage.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "list_executions",
            "Liste les exécutions de traitement récentes ou filtrées par état (queued, running, failed, completed).",
            new
            {
                type = "object",
                properties = new
                {
                    state = new { type = "string", description = "Filtrer par état (queued, running, failed, completed)." },
                    limit = new { type = "integer", description = "Nombre maximal de résultats (défaut 50, max 200).", @default = 50 }
                },
                required = Array.Empty<string>()
            }
        ),

        // --- 2. FLOWS ---
        new(
            "get_flow_schema",
            "Introspecte le catalogue de composants de flows disponibles (identifiants, types d'entrées/sorties, paramètres configurables) et fournit un exemple de flow JSON valide.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "list_flows",
            "Liste tous les flows configurés dans le cluster avec leurs versions publiées, leur type et statut de brouillon.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_flow",
            "Récupère la définition complète d'un flow (ou de son brouillon) par son identifiant UUID.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du flow." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "create_flow",
            "Crée et valide un nouveau flow à partir de sa définition JSON. Peut être publié immédiatement ou enregistré en brouillon. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Nom du flow." },
                    type = new { type = "string", description = "Type de média du flow (video, image, music, subtitles, generic).", @default = "video" },
                    publish = new { type = "boolean", description = "Si true, valide et publie le flow immédiatement en version 1.", @default = true },
                    nodes = new { type = "array", description = "Liste des nœuds du flow avec id, component (ou componentId), name et configuration." },
                    connections = new { type = "array", description = "Liste des connexions orientées entre sorties et entrées de nœuds." }
                },
                required = new[] { "nodes", "connections" }
            }
        ),
        new(
            "clone_flow",
            "Duplique un flow existant sous un nouveau nom. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    sourceFlowId = new { type = "string", description = "Identifiant UUID du flow à cloner." },
                    newName = new { type = "string", description = "Nom donné au nouveau flow dupliqué." }
                },
                required = new[] { "sourceFlowId", "newName" }
            }
        ),
        new(
            "delete_flow",
            "Supprime un flow du cluster (suppression logique préservant l'audit s'il a des versions publiées, définitive pour un brouillon). Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    flowId = new { type = "string", description = "Identifiant UUID du flow à supprimer." }
                },
                required = new[] { "flowId" }
            }
        ),

        // --- 3. SCRIPTS (JAVASCRIPT / PYTHON) ---
        new(
            "list_scripts",
            "Liste tous les scripts partagés réutilisables du cluster avec leur langage (javascript, python) et versions publiées.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_script",
            "Récupère le détail d'un script et le code source complet de son brouillon.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du script." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "save_script",
            "Crée ou met à jour le brouillon d'un script (javascript ou python). Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du script existant (omettre pour créer un nouveau script)." },
                    name = new { type = "string", description = "Nom du script (max 128 caractères)." },
                    language = new { type = "string", description = "Langage du script : 'javascript' ou 'python'.", @enum = new[] { "javascript", "python" } },
                    description = new { type = "string", description = "Description du rôle du script (max 1000 caractères)." },
                    code = new { type = "string", description = "Code source du script." },
                    expectedRevision = new { type = "integer", description = "Révision attendue pour mise à jour d'un script existant (défaut 1)." }
                },
                required = new[] { "name", "language", "code" }
            }
        ),
        new(
            "publish_script",
            "Publie une version immuable du brouillon d'un script pour utilisation en production. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du script." },
                    expectedRevision = new { type = "integer", description = "Révision de brouillon attendue." }
                },
                required = new[] { "id", "expectedRevision" }
            }
        ),

        // --- 4. PROFILS D'ENCODAGE ---
        new(
            "list_encoding_profiles",
            "Liste tous les profils de qualité d'encodage vidéo configurés dans le cluster.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_encoding_profile",
            "Récupère le détail d'un profil d'encodage (cibles par encodeur matériel/logiciel, qualité, presets).",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du profil d'encodage." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "save_encoding_profile",
            "Crée ou met à jour le brouillon d'un profil d'encodage vidéo. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du profil existant (omettre pour en créer un nouveau)." },
                    name = new { type = "string", description = "Nom du profil d'encodage." },
                    description = new { type = "string", description = "Description du profil." },
                    targets = new
                    {
                        type = "array",
                        description = "Liste des cibles d'encodage (encoderId, quality, preset, priority).",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                encoderId = new { type = "string" },
                                quality = new { type = "number" },
                                preset = new { type = "string" },
                                priority = new { type = "integer" }
                            },
                            required = new[] { "encoderId", "quality", "preset", "priority" }
                        }
                    },
                    expectedRevision = new { type = "integer", description = "Révision attendue pour mise à jour." }
                },
                required = new[] { "name", "targets" }
            }
        ),
        new(
            "publish_encoding_profile",
            "Publie une version immuable d'un profil d'encodage vidéo. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du profil." },
                    expectedRevision = new { type = "integer", description = "Révision de brouillon attendue." }
                },
                required = new[] { "id", "expectedRevision" }
            }
        ),

        // --- 5. MODÈLES DE FLOWS (TEMPLATES) ---
        new(
            "list_templates",
            "Liste tous les modèles de flows réutilisables du cluster.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_template",
            "Récupère le détail d'un modèle et sa définition de flow complète.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du modèle." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "create_template_from_flow",
            "Crée un modèle de flow réutilisable à partir d'une version publiée d'un flow existant. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    sourceFlowId = new { type = "string", description = "UUID du flow source." },
                    sourceVersion = new { type = "integer", description = "Numéro de version publiée du flow source." },
                    name = new { type = "string", description = "Nom du modèle créé." },
                    description = new { type = "string", description = "Description du modèle." }
                },
                required = new[] { "sourceFlowId", "sourceVersion", "name" }
            }
        ),

        // --- 6. PACKS DE COMPOSANTS ---
        new(
            "list_component_packs",
            "Liste tous les packs de composants (macro-composants réutilisables) disponibles dans le cluster.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_component_pack",
            "Récupère la définition, entrées et sorties d'un pack de composants.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du pack de composants." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "create_component_pack_from_flow",
            "Crée un pack de composants réutilisable à partir d'une séquence de nœuds d'un flow publié. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    sourceFlowId = new { type = "string", description = "UUID du flow source." },
                    sourceVersion = new { type = "integer", description = "Version publiée du flow." },
                    name = new { type = "string", description = "Nom du pack de composants." },
                    description = new { type = "string", description = "Description du pack." }
                },
                required = new[] { "sourceFlowId", "sourceVersion", "name" }
            }
        ),

        // --- 7. PLUGINS & INTÉGRATIONS EXTERNES ---
        new(
            "list_plugins",
            "Liste les plugins installés (Plex, Sonarr, Radarr, Jellyfin...) avec leurs versions et schémas de configuration.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_plugin_configuration",
            "Récupère la configuration globale d'un plugin (URL, réglages) et la liste des secrets configurés.",
            new
            {
                type = "object",
                properties = new
                {
                    pluginId = new { type = "string", description = "Identifiant du plugin (ex: plex, sonarr, radarr)." }
                },
                required = new[] { "pluginId" }
            }
        ),
        new(
            "save_plugin_configuration",
            "Enregistre la configuration globale d'un plugin (clés API, adresses serveur). Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    pluginId = new { type = "string", description = "Identifiant du plugin." },
                    expectedRevision = new { type = "integer", description = "Révision attendue (défaut 0)." },
                    values = new { type = "object", description = "Dictionnaire clé/valeur des paramètres de configuration." },
                    clearSecrets = new { type = "array", items = new { type = "string" }, description = "Liste facultative de secrets à effacer." }
                },
                required = new[] { "pluginId", "values" }
            }
        ),
        new(
            "test_plugin_connection",
            "Teste la connectivité réseau et les identifiants d'un plugin vers son service externe.",
            new
            {
                type = "object",
                properties = new
                {
                    pluginId = new { type = "string", description = "Identifiant du plugin." },
                    values = new { type = "object", description = "Valeurs de configuration à tester (facultatif, utilise la config enregistrée par défaut)." }
                },
                required = new[] { "pluginId" }
            }
        ),

        // --- 8. OUTILS MÉDIA & VALIDATION IA ---
        new(
            "list_media_tools",
            "Liste tous les outils média reconnus par le cluster (FFmpeg, FFprobe, MKVToolNix, MediaInfo, ImageMagick, Python, JS), leurs sous-commandes et leurs rôles.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_media_tool_info",
            "Fournit la documentation détaillée d'un outil média : sous-commandes, catalogue des encodeurs vidéo/audio (CPU et accélérations matérielles NVENC/QSV/VAAPI), presets et règles d'invocation sécurisées pour l'IA.",
            new
            {
                type = "object",
                properties = new
                {
                    toolId = new { type = "string", description = "Identifiant de l'outil (ex: ffmpeg, ffprobe, mkvtoolnix, mediainfo, imagemagick, python, javascript)." }
                },
                required = new[] { "toolId" }
            }
        ),
        new(
            "validate_tool_definition",
            "Valide un descripteur déclaratif tool.json pour l'intégration sécurisée d'un nouvel outil personnalisé au cluster (architecture 0064).",
            new
            {
                type = "object",
                properties = new
                {
                    definition = new { type = "object", description = "Objet tool.json contenant id, name, executable, probe et capabilitiesExposed." }
                },
                required = new[] { "definition" }
            }
        ),
        new(
            "validate_plugin_manifest",
            "Valide la conformité d'un manifeste plugin.json (namespace strict, runtime Python Landlock, descriptions sémantiques des sorties et schémas) pour l'intégration IA (architectures 0062 et 0063).",
            new
            {
                type = "object",
                properties = new
                {
                    manifest = new { type = "object", description = "Objet plugin.json décrivant le plugin et ses composants." }
                },
                required = new[] { "manifest" }
            }
        )
    ];

    public async Task<McpJsonRpcResponse?> HandleMessageAsync(McpJsonRpcRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "notifications/initialized" => null,
                "ping" => McpJsonRpcResponse.Success(request.Id, new { }),
                "tools/list" => McpJsonRpcResponse.Success(request.Id, new McpToolsListResult(DefinedTools)),
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

        if (!parameters.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } toolName)
            return McpJsonRpcResponse.Failure(request.Id, -32602, "Le nom de l'outil est requis dans tools/call");

        var arguments = parameters.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object
            ? argsProp
            : default;

        var isAdmin = user.IsInRole("admin");

        var callResult = toolName switch
        {
            // Bibliothèques, nœuds et exécutions
            "get_libraries_overview" => await ToolGetLibrariesOverviewAsync(ct),
            "list_nodes" => await ToolListNodesAsync(ct),
            "list_executions" => await ToolListExecutionsAsync(arguments, ct),

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

            // Outils média & validation IA
            "list_media_tools" => ToolListMediaTools(),
            "get_media_tool_info" => ToolGetMediaToolInfo(arguments),
            "validate_tool_definition" => ToolValidateToolDefinition(arguments),
            "validate_plugin_manifest" => ToolValidatePluginManifest(arguments),

            _ => McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true)
        };

        return McpJsonRpcResponse.Success(request.Id, callResult);
    }

    // --- IMPLÉMENTATIONS DES OUTILS ---

    private async Task<McpCallToolResult> ToolGetLibrariesOverviewAsync(CancellationToken ct)
    {
        var overview = await libraries.OverviewAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(overview, JsonOptions));
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
                new { id = "entry", component = "base-file", name = "Entrée Fichier" },
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
            var expectedRev = arguments.TryGetProperty("expectedRevision", out var revProp) && revProp.TryGetInt32(out var r) ? r : 1;
            var saved = await scripts.SaveDraftAsync(scriptId, new SaveFlowScriptDraftRequest(expectedRev, draft), ct);
            return saved is not null
                ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Brouillon de script mis à jour.", script = saved }, JsonOptions))
                : McpCallToolResult.Text("Conflit de révision ou script introuvable.", isError: true);
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

        if (arguments.TryGetProperty("id", out var idProp) && idProp.TryGetGuid(out var existingId))
        {
            var expectedRev = arguments.TryGetProperty("expectedRevision", out var revProp) && revProp.TryGetInt32(out var r) ? r : 1;
            var saved = await profiles.SaveDraftAsync(existingId, new SaveEncodingProfileDraftRequest(expectedRev, draft), ct);
            return saved is not null
                ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Brouillon de profil d'encodage mis à jour.", profile = saved }, JsonOptions))
                : McpCallToolResult.Text("Conflit de révision ou profil introuvable / intégré.", isError: true);
        }
        else
        {
            var newId = Guid.NewGuid();
            var created = await profiles.CreateAsync(newId, draft, ct);
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
}
