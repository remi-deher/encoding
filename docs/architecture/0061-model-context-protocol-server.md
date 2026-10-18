# 0061 — Serveur Model Context Protocol (MCP) pour automatisation IA

Statut : implémentée. Transport HTTP/SSE (`/api/v1/mcp/sse`), HTTP direct (`/api/v1/mcp`) et commande stdio (`mcp`).

## Besoin

Permettre à n'importe quel assistant d'IA (Antigravity, Cursor, Claude Desktop, ChatGPT, agents autonomes) de piloter le cluster Encoding selon le standard ouvert **Model Context Protocol (MCP)**, sans nécessiter d'installer un runtime supplémentaire sur la machine de l'utilisateur, ni de lancer un second conteneur Docker.

## Architecture

Le serveur MCP est embarqué directement dans l'application backend ASP.NET Core (.NET 10) et l'image Docker unique du projet :

```text
┌─────────────────────────────────┐
│     Client MCP (IA / IDE)       │
│  (Antigravity, Claude, Cursor)  │
└──────────────┬──────────────────┘
               │
               │ HTTP/SSE avec Bearer Token (ou stdio via docker exec)
               ▼
┌─────────────────────────────────────────────────────────┐
│              Conteneur Docker unique                    │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │             API ASP.NET Core (.NET 10)            │  │
│  │                                                   │  │
│  │  - /api/v1/mcp/sse (GET stream SSE)               │  │
│  │  - /api/v1/mcp/message (POST JSON-RPC)            │  │
│  │  - /api/v1/mcp (POST direct JSON-RPC)             │  │
│  │                                                   │  │
│  │  ┌─────────────────────────────────────────────┐  │  │
│  │  │                 McpService                  │  │  │
│  │  │  (tools/list, tools/call, ping, initialize) │  │  │
│  │  └──────────────────────┬──────────────────────┘  │  │
│  │                         │                         │  │
│  │     ┌───────────────────┼───────────────────┐     │  │
│  │     ▼                   ▼                   ▼     │  │
│  │ FlowRepository  LibraryRepository  NodeRepository │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

## Primitives et outils exposés

Le serveur MCP implémente la spécification JSON-RPC 2.0 (protocole `2024-11-05`) et expose 9 outils intégrés :

| Outil | Description | Rôle requis |
| :--- | :--- | :--- |
| `get_libraries_overview` | Vue d'ensemble consolidée de toutes les bibliothèques (flows associés, modes d'accès par nœud, état des scans, compteurs). | Lecture (`viewer`) |
| `get_flow_schema` | Introspection des composants disponibles, paramètres configurables, entrées/sorties et exemple JSON de flow. | Lecture (`viewer`) |
| `list_flows` | Liste des flows configurés dans le cluster avec leurs versions et statuts. | Lecture (`viewer`) |
| `get_flow` | Définition détaillée d'un flow (ou de son brouillon) par UUID. | Lecture (`viewer`) |
| `create_flow` | Création et validation d'un flow depuis sa définition JSON (avec publication immédiate optionnelle). | Écriture (`admin`) |
| `clone_flow` | Duplication d'un flow existant sous un nouveau nom. | Écriture (`admin`) |
| `delete_flow` | Suppression d'un flow (suppression logique pour les flows publiés, définitive pour les brouillons). | Écriture (`admin`) |
| `list_nodes` | Liste des nœuds du cluster, rôles (serveur, worker), état de présence et capacités d'encodage. | Lecture (`viewer`) |
| `list_executions` | Liste des exécutions de traitement récentes ou filtrées par état (`queued`, `running`, `failed`, `completed`). | Lecture (`viewer`) |

## Modes de connexion

### 1. Transport HTTP / Server-Sent Events (SSE)
Connexion standard pour clients distants ou locaux sans processus local :
- **Endpoint SSE** : `GET /api/v1/mcp/sse` (en-tête `Authorization: Bearer <jeton_cluster>`).
- Émet immédiatement l'événement `endpoint` avec l'URL de message (`/api/v1/mcp/message?sessionId={id}`).
- Les requêtes JSON-RPC sont envoyées via `POST /api/v1/mcp/message?sessionId={id}` et les réponses sont acheminées sur le flux SSE.

### 2. Transport HTTP direct
Pour les clients simples ou scripts d'automatisation :
- `POST /api/v1/mcp` avec corps JSON-RPC `{"jsonrpc": "2.0", "id": 1, "method": "tools/call", ...}`.
- Renvoie directement la réponse JSON-RPC dans le corps HTTP 200 OK.

### 3. Transport en ligne de commande (stdio)
Pour les clients locaux ou intégrations dans des outils CLI :
- Commande : `dotnet Encoding.Api.dll mcp` ou `encoding mcp` (via `docker exec -i <container> dotnet Encoding.Api.dll mcp`).
- Variables d'environnement : `ENCODING_API_URL` (défaut `http://127.0.0.1:5000`) et `ENCODING_API_TOKEN` (ou options `--url` et `--token`).
- Lit les requêtes JSON-RPC sur `stdin` et écrit les réponses sur `stdout`.

## Configuration d'un client MCP (Exemple)

```json
{
  "mcpServers": {
    "encoding": {
      "url": "http://localhost:5000/api/v1/mcp/sse",
      "headers": {
        "Authorization": "Bearer enc_pat_..."
      }
    }
  }
}
```

## Validation

Vérifiée dans `ApiImprovementsScenarios` (`Runtime.Tests`) :
- Négociation de protocole (`initialize`).
- Découverte du catalogue d'outils (`tools/list`).
- Invocation d'introspection (`tools/call` sur `get_flow_schema`).
- Création et publication directe d'un flow JSON (`tools/call` sur `create_flow`).
- Suppression d'un flow via MCP (`tools/call` sur `delete_flow`).
- Établissement du flux SSE (`/api/v1/mcp/sse`), échange d'événement `endpoint` et acheminement bidirectionnel de messages (`/api/v1/mcp/message`).
