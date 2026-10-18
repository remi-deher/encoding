# 0062 — Guide de conception des plugins et composants pour l'exploitation par IA et MCP

Statut : référence technique. Complète [0011](0011-flow-block-template.md), [0013](0013-flow-catalog-and-validation.md), [0019](0019-plugin-configuration.md), [0032](0032-integration-plugins-and-sdk.md) et [0061](0061-model-context-protocol-server.md).

## 1. Contexte & Enjeux

Dans l'architecture Encoding, les plugins tiers (ex. Plex, Sonarr, Jellyfin, règles de transcodage personnalisées) s'exécutent dans des processus isolés (bac à sable Landlock, SDK Python dans `backend/plugins/`).

Un agent d'IA ou un client **Model Context Protocol (MCP)** ne lit jamais le code exécutable interne du plugin. Pour comprendre comment utiliser un composant, comment brancher ses entrées/sorties et comment configurer ses paramètres, le modèle d'IA s'appuie **exclusivement** sur :
1. Le manifeste déclaratif du plugin (`PluginManifest`).
2. Les descripteurs de composants (`FlowComponentDescriptor`).
3. Le schéma des champs et des sorties (`FlowConfigurationSchema` & `Outputs`).

> [!IMPORTANT]
> Tout composant dont les descriptions sont vagues, absentes ou purement visuelles conduira inévitablement l'IA à des hallucinations, des inversions de sorties (ex: brancher l'échec sur la publication) ou des valeurs de paramètres invalides.

---

## 2. Anatomie d'un composant optimisé pour l'IA

Chaque composant déclaré dans le manifeste d'un plugin doit renseigner méticuleusement les champs suivants :

```json
{
  "id": "myplugin-audio-filter",
  "version": "1.0.0",
  "name": "Filtrage et Normalisation Audio",
  "description": "Analyse les pistes audio du média, convertit les flux secondaires en stéréo AAC et normalise le volume sonore selon les normes EBU R128.",
  "kind": "action",
  "mediaType": "audio",
  "supportedFlowMediaTypes": ["video", "audio"],
  "requiresMedia": true,
  "requiresWriteAccess": true,
  "outputs": [ ... ],
  "configurationSchema": { ... }
}
```

### Règle d'or sur la description du composant
* **Objectif** : Dire ce que fait le bloc, ce qu'il prend en entrée, et l'effet produit sur le fichier média ou les métadonnées.
* **À éviter** : *"Bloc pour l'audio"*, *"Mon filtre v2"*.
* **À privilégier** : *"Supprime les sous-titres indésirables selon une liste blanche de langues (ISO 639-2) et conserve les pistes forcées."*

---

## 3. La Sémantique des Sorties (`Outputs`) : Clé du Routage

L'IA assemble des flows sous forme de graphes orientés acycliques (DAG). Elle doit savoir avec certitude **quelle branche emprunter** selon le résultat du traitement.

Chaque sortie doit impérativement définir un `id` stable, un `label` court et une `description` sémantique expliquant la condition de déclenchement :

| Sortie type | `id` | `label` | `description` recommandée pour l'IA |
| :--- | :--- | :--- | :--- |
| Succès nominal | `success` | Conforme / Modifié | *"Le fichier a été transformé avec succès et la sortie temporaire est validée."* |
| Déjà conforme | `not-modified` | Déjà conforme | *"Le média respectait déjà toutes les règles ; aucune ré-encodage n'a été nécessaire."* |
| Détection / Condition | `match` | Correspond | *"Au moins une piste correspond aux critères de recherche spécifiés."* |
| Alternative | `no-match` | Aucune correspondance | *"Aucune piste ne correspond aux critères ; le traitement doit emprunter le chemin alternatif."* |
| Erreur récupérable | `error` | Erreur | *"Échec du traitement du fichier (ex: codec corrompu) ; le fichier source est préservé intact."* |
| Délai / Attente | `busy` | Occupé | *"Ressource ou verrou indisponible actuellement (ex: disque occupé) ; réessai ultérieur sans blocage."* |

> [!WARNING]
> Ne jamais utiliser des identifiants de sorties cryptiques (`out_1`, `out_2`) sans description. L'IA risquerait d'inverser la branche d'échec et la branche de publication.

---

## 4. Le Schéma de Configuration déclaratif (`ConfigurationSchema`)

Pour que l'IA puisse générer les paramètres du bloc dans un flow JSON (via `create_flow`), les champs (`FlowConfigurationField`) doivent être auto-documentés :

### 1. Typage rigoureux
Toujours utiliser le type adéquat : `text`, `number`, `boolean`, `select`.

### 2. Unités et bornes (`Min`, `Max`, `Step`)
Toujours indiquer l'unité de mesure dans la description et borner les nombres :
```json
{
  "id": "crf",
  "type": "number",
  "label": "Facteur de qualité (CRF)",
  "description": "Valeur Constant Rate Factor pour l'encodeur x265. Entre 0 (sans perte) et 51 (qualité minimale). Recommandé : 22 à 28.",
  "required": true,
  "defaultValue": 24,
  "min": 0,
  "max": 51,
  "step": 1
}
```

### 3. Valeurs par défaut obligatoires (`DefaultValue`)
Fournir systématiquement une valeur par défaut fonctionnelle et sécurisée. Si l'IA ne mentionne pas explicitement un champ facultatif, le composant doit pouvoir s'exécuter sainement avec sa valeur par défaut.

### 4. Choix fermés (`Options`)
Pour tout paramètre acceptant un jeu limité de valeurs, utiliser `select` avec la liste complète des options :
```json
{
  "id": "targetCodec",
  "type": "select",
  "label": "Codec cible",
  "description": "Codec audio à appliquer pour le ré-échantillonnage.",
  "defaultValue": "aac",
  "options": [
    { "value": "aac", "label": "AAC (Stéréo universel)" },
    { "value": "ac3", "label": "Dolby Digital AC-3 (5.1 surround)" },
    { "value": "eac3", "label": "Dolby Digital Plus E-AC-3" },
    { "value": "opus", "label": "Opus (Haute efficacité)" }
  ]
}
```

---

## 5. Exemple complet de déclaration d'un composant de plugin

Voici un exemple de fichier `manifest.json` respectant parfaitement ces directives :

```json
{
  "contractVersion": 1,
  "id": "plex",
  "version": "1.0.0",
  "name": "Plex Media Server Integration",
  "supportedHostContractVersions": [1],
  "components": [
    {
      "id": "plex-playback-guard",
      "version": "1.0.0",
      "name": "Garde de lecture Plex",
      "description": "Interroge le serveur Plex pour vérifier si le média est en cours de lecture par un utilisateur avant d'autoriser le remplacement de la source.",
      "kind": "condition",
      "mediaType": "video",
      "supportedFlowMediaTypes": ["video"],
      "requiresMedia": true,
      "requiresWriteAccess": false,
      "outputs": [
        {
          "id": "allow",
          "label": "Remplacement autorisé",
          "description": "Aucun utilisateur ne lit ce média actuellement (ou l'épisode précédent). Le remplacement peut avoir lieu sans coupure."
        },
        {
          "id": "wait",
          "label": "Lecture en cours",
          "description": "Le média est en cours de lecture sur un client Plex. Le flow doit temporiser sans modifier le fichier."
        },
        {
          "id": "refuse",
          "label": "Doute ou indisponible",
          "description": "Impossible de joindre le serveur Plex ou incertitude sur l'état de lecture. Par sécurité, le remplacement est refusé."
        }
      ],
      "configurationSchema": {
        "version": 1,
        "fields": [
          {
            "id": "maxWaitMinutes",
            "type": "number",
            "label": "Attente maximale",
            "description": "Durée maximale en minutes pendant laquelle le flow attend que la lecture se termine (1 à 120 minutes).",
            "required": true,
            "defaultValue": 30,
            "min": 1,
            "max": 120
          },
          {
            "id": "checkPreviousEpisode",
            "type": "boolean",
            "label": "Surveiller l'épisode précédent",
            "description": "Si coché, vérifie également si un utilisateur est en train de visionner l'épisode qui précède immédiatement celui-ci dans la saison.",
            "required": false,
            "defaultValue": true
          }
        ]
      }
    }
  ]
}
```

---

## 6. Découverte dynamique via MCP

Grâce au service MCP implémenté ([0061](0061-model-context-protocol-server.md)) :
1. Dès qu'un fichier manifeste de plugin est déposé sur le serveur, `FlowComponentRegistry` l'intègre au démarrage.
2. L'outil MCP `get_flow_schema` renvoie instantanément à l'IA la liste des composants, leurs descriptions, leurs sorties et leurs schémas de paramètres.
3. L'outil MCP `create_flow` accepte directement l'identifiant simplifié (ex: `"component": "plex-playback-guard"`), résout automatiquement le descripteur et valide la configuration contre le schéma.

**Aucune ligne de code C# ni aucune configuration MCP supplémentaire n'est requise lors de l'ajout d'un nouveau plugin.**
