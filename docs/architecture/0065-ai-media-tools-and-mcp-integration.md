# 0065 — Intégration des outils média dans le serveur MCP et règles pour agents IA

Statut : décision d'architecture et implémentation. Complète [0005](0005-media-tools-in-flow-components.md), [0054](0054-script-tools-helper.md), [0061](0061-model-context-protocol-server.md), [0062](0062-plugin-and-component-authoring-for-ai-and-mcp.md), [0063](0063-plugin-store-and-repositories.md) et [0064](0064-custom-and-managed-media-tools.md).

---

## 1. Contexte & Objectifs

Pour permettre à un agent d'intelligence artificielle (Antigravity, Cursor, Claude Desktop ou tout client Model Context Protocol) de :
1. Concevoir des flows complexes sans erreur de syntaxe ou de paramètres sur les outils média (`ffmpeg`, `mkvmerge`, etc.).
2. Rédiger des scripts personnalisés utilisant l'aide `tools.run()` sous Landlock (0054).
3. Concevoir et packager de nouveaux plugins conformes (0062, 0063) et déclarer de nouveaux outils personnalisés (0064).
4. Valider interactivement ses manifestes avant de les déployer sur le cluster.

Le serveur MCP du projet ([0061](0061-model-context-protocol-server.md)) s'enrichit de 4 outils dédiés aux **Outils média** et à la **Validation IA**.

---

## 2. Nouveaux outils exposés sur le serveur MCP

| Outil MCP | Description | Rôle requis |
| :--- | :--- | :--- |
| `list_media_tools` | Liste les outils média canoniques reconnus par le cluster (`ffmpeg`, `ffprobe`, `mkvtoolnix`, `mediainfo`, `imagemagick`, `python`, `javascript`), leurs sous-commandes et leurs rôles. | Lecture (`viewer`) |
| `get_media_tool_info` | Introspecte un outil spécifique (`toolId`) : sous-commandes, catalogue complet des encodeurs vidéo supportés (`VideoEncoderCatalog`), options de qualité, presets et directives de sécurité. | Lecture (`viewer`) |
| `validate_tool_definition` | Valide un descripteur déclaratif `tool.json` (ID slug, commande de version, regex compilable, capacités annoncées) selon 0064. | Lecture (`viewer`) |
| `validate_plugin_manifest` | Valide un manifeste `plugin.json` (namespace strict, runtime Python Landlock, descriptions sémantiques obligatoires des sorties de composants) selon 0062 et 0063. | Lecture (`viewer`) |

---

## 3. Guide de branchement IA sur les Outils Média

Pour toute génération de script ou de flow pilotant des outils média, l'IA doit appliquer les règles suivantes :

### 1. FFmpeg & FFprobe
- **Invocation via `tools.run`** :
  ```javascript
  // Dans un base-script (useTools: true) :
  const res = tools.run("ffmpeg", ["-hide_banner", "-nostats", "-i", "input.mkv", "-c:v", "copy", "-c:a", "aac", "output.mkv"]);
  if (!res.ok) throw new Error("Échec FFmpeg : " + res.stderr);
  ```
- **Flags indispensables** :
  - Toujours spécifier `-hide_banner` et `-nostats` pour préserver le journal d'exécution.
  - Spécifier `-y` pour écraser la sortie temporaire de travail.
  - Pour `ffprobe`, toujours utiliser le format JSON standard :
    `["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", "input.mkv"]`

### 2. MKVToolNix (`mkvmerge`, `mkvextract`, `mkvpropedit`)
- **Codes de retour particuliers** :
  - `mkvmerge` renvoie **0** pour un succès parfait, et **1** lorsqu'il y a des avertissements mineurs (par exemple une piste de sous-titre vide ou un horodatage ajusté). Ce code 1 est considéré comme un succès par l'infrastructure Encoding.
  - Code > 1 : erreur bloquante.
- **Modification en place** :
  - Utiliser `mkvpropedit` pour retoucher les titres, flags de langues ou pistes par défaut sur la copie de travail sans réécrire l'intégralité du fichier.

### 3. MediaInfo
- Toujours appeler avec `["--Output=JSON", "input.mkv"]` pour obtenir une arborescence JSON exploitable directement en JavaScript ou Python.

---

## 4. Workflow de validation interactive pour l'IA

Lorsqu'une IA est invitée à créer un plugin ou intégrer un nouvel outil :

```text
┌─────────────────────────────────┐
│     Agent IA (IDE / Chat)       │
└────────────────┬────────────────┘
                 │
                 │ 1. Rédige le plugin.json ou tool.json selon les règles
                 ▼
┌─────────────────────────────────┐
│   Appel MCP : validate_*        │
│   (validate_plugin_manifest)    │
└────────────────┬────────────────┘
                 │
                 │ 2. Retourne { isValid: bool, errors: [], suggestions: [] }
                 ▼
┌─────────────────────────────────┐
│   Correction automatique si     │
│   sortie non documentée ou      │
│   namespace invalide            │
└────────────────┬────────────────┘
                 │
                 │ 3. Déploiement sécurisé sans runtime crash
                 ▼
┌─────────────────────────────────┐
│      Cluster Encoding           │
└─────────────────────────────────┘
```

Ce couplage élimine tout risque de corruption de flux ou d'inversion logique de branches dans les graphes de flows.
