# 0072 — Générateur de flows par IA, Copilot et boucle d'auto-réparation en backend

Statut : décision d'architecture. Complète [0010](0010-structured-flow-editor.md), [0011](0011-flow-block-template.md), [0013](0013-flow-catalog-and-validation.md), [0062](0062-plugin-and-component-authoring-for-ai-and-mcp.md) et [0071](0071-ai-cli-tools-and-headless-authentication.md).

---

## 1. Contexte & Objectifs

L'éditeur de flows structuré d'Encoding permet de composer des pipelines sophistiqués (branches conditionnelles, règles audio, sous-titres, transcodage adaptatif, gardes de lecture Plex). Cependant, pour un utilisateur néophyte ou lors de la création initiale d'un graphe complexe, assembler manuellement 15 à 25 blocs interconnectés reste fastidieux.

Pour rendre la conception de flux accessible en langage naturel directement dans l'application web (sans dépendre d'un client externe Model Context Protocol) :
1. Le backend propose un service de **génération et de modification de flows assisté par IA**.
2. Il utilise le fournisseur IA configuré sur le worker ou le cluster (Codex CLI / Claude CLI selon [0071](0071-ai-cli-tools-and-headless-authentication.md), ou Ollama local).
3. Il intègre une **barrière de conformité avec auto-réparation** s'appuyant sur [FlowDefinitionValidator](0013-flow-catalog-and-validation.md) pour garantir qu'aucun graphe invalide ou corrompu ne parvienne à l'interface ou au runtime.

---

## 2. Décisions d'Architecture

### A. Le Constructeur de Contexte Catalogue (`FlowCatalogPromptBuilder`)
Pour que le modèle de langage comprenne exactement les composants disponibles sans saturer sa fenêtre de contexte :
1. Le service filtre le catalogue `FlowComponentRegistry` selon le `mediaType` ciblé (`video`, `audio`, etc.) et les composants `common`.
2. Grâce aux descriptions sémantiques normalisées en [0062](0062-plugin-and-component-authoring-for-ai-and-mcp.md), il génère une vue compacte au format JSON/YAML comprenant pour chaque composant :
   - Son identifiant unique (`id`) et sa version (`version`).
   - La liste exacte de ses sorties autorisées (`outputs`: `id`, `label`).
   - La liste de ses champs de configuration avec types, valeurs par défaut et énumérations acceptées.
3. Le schéma structurel de `FlowDefinition` (version 1, liste `nodes`, liste `connections`, contraintes d'acyclisme et entrée unique obligatoire) est injecté en instructions système strictes.

---

### B. La boucle d'Exécution & Auto-Réparation (`Generate -> Validate -> Self-Repair`)

Le workflow de génération s'exécute entièrement côté serveur pour isoler l'utilisateur des erreurs de syntaxe :

```text
┌────────────────────────────────────────────────────────┐
│               Requête Utilisateur (API)                │
│    "Créer un flow 4K vers 1080p avec audio AAC"        │
└───────────────────────────┬────────────────────────────┘
                            │
                            ▼
┌────────────────────────────────────────────────────────┐
│        Passage 1 : Génération Initiale (Prompt)        │
│    Invocateur CLI (Codex / Claude) -> Reçoit JSON      │
└───────────────────────────┬────────────────────────────┘
                            │
                            ▼
┌────────────────────────────────────────────────────────┐
│            Contrôle FlowDefinitionValidator           │
│   Vérifie nœuds, types, sorties, schéma et acyclisme   │
└───────────────┬────────────────────────┬───────────────┘
                │                        │
       [Si 100% Valide]          [Si Erreurs détectées]
                │                        │
                │                        ▼
                │        ┌─────────────────────────────────────────────────┐
                │        │  Passage 2 : Auto-réparation (Prompt Erreurs)   │
                │        │  "Le flow a échoué avec les codes :             │
                │        │   - connection.output.unknown sur node 'audio'  │
                │        │   - field.unknown 'bitrate' sur transcode       │
                │        │   Corrige le JSON et renvoie l'objet valide."   │
                │        └───────────────────────┬─────────────────────────┘
                │                                │
                │                                ▼
                │        ┌─────────────────────────────────────────────────┐
                │        │      Second Contrôle FlowDefinitionValidator    │
                │        └───────────────────────┬─────────────────────────┘
                │                                │
                ▼                                ▼
┌────────────────────────────────────────────────────────┐
│      Réponse HTTP 200 avec FlowDefinition validé       │
│    (Projeté directement dans le canvas CDK Angular)    │
└────────────────────────────────────────────────────────┘
```

- **Seuil d'abandon** : Le cycle d'auto-réparation est borné à 2 tentatives maximum. En cas d'échec persistant, l'API retourne une erreur structurée HTTP 422 avec les motifs exacts sans corrompre l'état courant de l'éditeur.

---

### C. Contrats d'API Dédiés

```csharp
// POST /api/v1/flows/ai/generate
public sealed record FlowAiGenerateRequest(
    string MediaType,
    string UserPrompt);

// POST /api/v1/flows/ai/refactor
public sealed record FlowAiRefactorRequest(
    FlowDefinition CurrentFlow,
    string UserPrompt);

// POST /api/v1/flows/ai/explain
public sealed record FlowAiExplainRequest(
    FlowDefinition Flow);

public sealed record FlowAiResponse(
    bool Success,
    FlowDefinition? Flow,
    string? Explanation,
    IReadOnlyList<FlowValidationIssue>? ValidationIssues);
```

1. **`POST /api/v1/flows/ai/generate`** : Génère un nouveau graphe complet à partir d'une description textuelle.
2. **`POST /api/v1/flows/ai/refactor`** : Reçoit le graphe en cours d'édition et applique une modification ciblée demandée par l'utilisateur (ex. *« Ajoute une vérification de lecture Plex avant de remplacer le fichier »* ou *« Insère un contrôle de taille de fichier pour éviter qu'il ne grossisse »*).
3. **`POST /api/v1/flows/ai/explain`** : Génère une explication textuelle pédagogique en français décrivant étape par étape le comportement d'un flow complexe.

---

### D. Expérience Utilisateur dans l'Éditeur Angular

1. **Bouton d'action « Assistant IA »** dans la barre d'outils de l'éditeur de flows (`frontend/src/app/flows/editor/`).
2. **Dialogue de génération rapide** :
   - Champ de texte libre multi-lignes avec suggestions prédéfinies (*« Anime japonais »*, *« Réencodage HEVC standard »*, *« Rognage bandes noires + Audio 5.1 »*).
   - Bouton `Générer le flow`.
   - Indicateur de progression avec statuts clairs (*« Analyse du besoin... »*, *« Assemblage des blocs... »*, *« Vérification de conformité... »*).
3. **Prévisualisation & Application** :
   - L'utilisateur visualise le nouveau graphe généré dans le canvas.
   - Possibilité d'annuler ou de valider l'import.

---

## 3. Sécurité et Invariants

1. **Aucun enregistrement automatique** : L'API d'IA ne publie ni ne persiste aucun flow en base de données. Elle retourne uniquement un brouillon validé prêt à être examiné et sauvegardé par l'utilisateur.
2. **Respect des règles d'isolation matérielle** : Le constructeur de prompt rappelle formellement que les outils matériels conteneur (`Stock`) restent imposés pour tout encodage accéléré par GPU.
3. **Neutralité technologique de l'invocateur** : Le service utilise l'interface commune `IAiTextClient`, permettant d'exécuter la génération soit via **Codex CLI / Claude CLI** (0071), soit via un modèle local **Ollama** (`llama3.2`, `mistral-nemo`), soit via une clé API externe.
