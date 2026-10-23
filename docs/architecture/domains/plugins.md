---
domain: plugins
updated: 2026-10-21
legacy_adr: [0005, 0011, 0019, 0020, 0021, 0024, 0032, 0054, 0055, 0056, 0057, 0058, 0059, 0062, 0063, 0064, 0065, 0066, 0067, 0068, 0069, 0070, 0075, 0076, 0079]
code:
  - backend/src/Encoding.Api/Flows/Catalog/*.json
  - backend/src/Encoding.Api/Flows/FlowComponentRegistry.cs
  - backend/src/Encoding.Api/Plugins/
  - backend/src/Encoding.Core/Plugins/
  - backend/src/Encoding.Contracts/Plugins/
  - backend/plugins/ (SDK Python/JS, plugins Sonarr/Radarr/Plex/Jellyfin, exemples)
tags: [plugin, composant, manifeste, store, dépôt, webhook, sdk, outils média, landlock, python, catalogue]
---

# Plugins, composants et outils média

Le **catalogue de composants fait foi dans le code**, pas dans une doc : lire `backend/src/Encoding.Api/Flows/Catalog/<plugin>.json`. Versions au 2026-10-22 : base 1.8.0, nodes 1.3.0, conditions 1.2.0, video 1.2.0, audio 1.1.0, subtitles 1.2.0, mkv 1.1.0, images 1.0.0, probe 1.0.0, ai 1.0.0, plex 1.1.0, sonarr/radarr/jellyfin 1.0.0. Les anciens identifiants `base-*` (transcode, audio, sous-titres, remux, auto-crop…) sont aujourd'hui répartis dans `video`, `audio`, `subtitles` et restent acceptés par le runtime (`MediaActions`) ; `base-condition` n'est plus proposé (descripteur historique dans `Flows/Legacy/`).

## Contrat
- Un plugin déclare identité, version, compatibilité hôte (`supportedHostContractVersions`) et composants (`PluginManifest`, `FlowComponentDescriptor`). Un composant : `id`, `version`, `kind` (`action|condition|…`), types de flow compatibles, `requiresMedia`/`requiresWriteAccess`, **sorties** (`id`, `label`, `description`) et `configurationSchema` déclaratif v1 (`text|number|boolean|select|condition-tree|record-list…`, `inline`, `importance`, `section`, visibilité conditionnelle, bornes, défauts). **Aucun code d'interface** dans un plugin ; le frontend rend les schémas avec le formulaire commun.
- Pour que l'IA et MCP assemblent des flows sans erreur : description claire du composant, **chaque sortie et chaque champ décrits** (unités, bornes, défaut sûr), ids de sortie stables (`success`, `error`, `unchanged`…). Un composant sans descriptions est un défaut.
- Évolution : tout changement de comportement incrémente la version du plugin, garde les versions précédentes résolubles (`compatiblePluginVersions`) et un worker n'annonçant pas la version requise (`plugin/<id>` + version) ne reçoit pas le flow (pendant une mise à jour progressive les flows attendent un worker à jour). Un composant ou une option peut exiger une version minimale.
- Configuration à deux niveaux : **plugin** (défauts du cluster) puis **dérogation par flow** (révision propre), plus les paramètres d'**instance** du bloc. Valeurs effectives fusionnées à l'attribution et figées pour la tâche. Secrets chiffrés en base, jamais dans exports/sauvegardes/événements/journaux ; un champ `"scope": "server"` ne quitte jamais le serveur (ex. `webhookSecret`). Test de connexion depuis le serveur (ne prouve pas l'accès depuis les workers).

## Exécution et isolation
- Plugins tiers : **processus séparé** (jamais chargés dans ASP.NET ou l'agent), protocole stdin/stdout versionné (`describe`, `execute`, `contractVersion` 1), réponses bornées, sous **Landlock** ([runtime.md](runtime.md)). Runtimes : `python3` (stdlib, SDK `encoding_plugin_sdk.py`) et `node` (SDK `encoding-plugin-sdk.mjs`). Pas de plugin .NET : jamais démarré sous Landlock réel, non vérifié.
- `ProcessSandbox.MinimalEnvironment` : liste blanche d'environnement. `/proc` et `/sys` sont lisibles dans le bac à sable (décision de sécurité ouverte : [open-questions.md](../open-questions.md)).
- La capacité `plugin/<id>` n'est annoncée qu'après poignée de main réussie (avec empreinte du contrat comparée au serveur).

## Store de plugins (implémenté, PR #205)
- Paquet `.zip` : `plugin.json` à la racine = `PluginManifest` + bloc `runtime` (`{type: python3|node, entrypoint}`) ; identifiant qualifié `espace.nom` ; limites 32 Mo compressé, 128 Mo décompressé, 500 fichiers ; chemins absolus, `..`, `:` refusés. `official.*` réservé au dépôt officiel.
- Dépôts statiques `repository.json` 1.x (validés entrée par entrée, SHA-256 obligatoire, versions publiées immuables). **Le dépôt officiel n'existe que si `PluginStore:OfficialRepositoryUrl` est configuré** ; aucune adresse n'est codée en dur.
- Serveur : installation à chaud (`FlowComponentRegistry.TryInstall`), stockage `PluginStore:Directory`, empreinte vérifiée au démarrage. Workers : `PluginSyncService` (chaque minute) télécharge depuis `GET /api/v1/store/installed`, vérifie le SHA-256, extrait dans `Runtime:CustomPluginDirectory` puis écrit `.verified`. Mises à jour **manuelles**, jamais automatiques.
- Limites : l'état du Store est **local à chaque serveur** (non réconcilié) ; pas de désactivation sans retrait, pas de refus de retrait si un flow l'utilise, pas de signature, `minEngineVersion`/`requiredTools` non contrôlés, synchronisation périodique des dépôts non faite, rien exécuté sous Landlock réel.
- Outils d'auteur : validateur de manifeste/paquet, simulation locale, SDK JavaScript ; MCP `validate_plugin_manifest`, `validate_tool_definition`, `list_media_tools`, `get_media_tool_info` ([auth-ops.md](auth-ops.md)). Guide : `backend/plugins/README.md`.

## Plugins intégrés (rôle, pas liste exhaustive)
- **base** : entrées (fichier générique + spécialisées), `base-script` (JS/Python, `useTools`), plan de traitement (`prepare`/`execute`), report, fin, variable, `base-wait`, `base-disk-lock/unlock`. Installé et actif par défaut.
- **nodes** : contraintes d'attribution, `nodes-handoff`, `nodes-retry-on-worker-loss`, `nodes-require-direct-access` ([runtime.md](runtime.md)). Reconnu nativement par les workers.
- **conditions** : `conditions-match` (arbre ET/OU) + blocs dédiés Oui/Non/Erreur (codec vidéo/audio, résolution avec tolérance aux ratios cinéma, canaux, langue, HDR/Dolby Vision, profondeur de couleur) évalués par `MediaConditionEvaluator` sur les métadonnées scannées.
- **video / audio / subtitles / mkv / images / probe** : opérations média ; points d'attention durables : transcodage vidéo adaptatif (paliers de qualité par résolution, 10 bits automatique pour HEVC/AV1, préservation HDR par défaut), `audio-rules` (table fixe stéréo AAC / 3 canaux et plus en AC-3 ≤ 6 canaux avec LFE préservé, déduction de disposition refusée en cas de références contradictoires), noms de pistes MKV via `mkvpropedit` sur la copie de travail (sélection par UID), `mkv-edit-in-place` (langue, nom, drapeaux d'une piste modifiés dans la source, ADR 0002), sous-titres (SRT→ASS au profil proportionnel intégré « Crunchyroll », ASS personnalisé laissé intact, polices depuis `ENCODING_FONT_DIR` ou, depuis `subtitles` 1.3.0, depuis une ressource du catalogue du cluster (champ `resource`, « identifiant:version » ; le worker télécharge les fichiers du serveur avec son jeton de nœud, contrôle taille et SHA-256, ajoute toutes les polices ; aucun jeu local n'est alors exigé à l'éligibilité ; ADR 0006) ; non versionnées dans le dépôt), `probe-validate-output`, garde de taille, auto-crop, désentrelacement.
- **ai** : `ai-translate-subtitles`, `ai-metadata-enricher` ([ai.md](ai.md)).
- **plex / sonarr / radarr / jellyfin** (Python, image worker `/opt/encoding/plugins/*`) : actions de flow ; **déclencheur d'import** = webhook côté serveur `POST /api/v1/integrations/{sonarr|radarr}/webhook` (secret en temps constant, fermé sans secret, 1 Mo, limitation 10 échecs/5 min, demande durable `integration_trigger_requests`, passe par la découverte et la mise en file communes ; un chemin du réseau n'est jamais exécuté sans avoir été découvert). Correspondance de chemins `LibraryPathMapping` (une définition C#, mêmes vecteurs côté Python). Clés API uniquement en en-tête, jamais dans les messages. Plex : lecture en cours (option épisode précédent), `plex-analyze-media`, garde de publication. Échec d'infrastructure = erreur de processus, jamais une sortie métier.

## Outils média : rôles et fournisseurs
- Les composants consomment un **rôle d'outil** (`ffmpeg`, `ffprobe`, `mkvmerge`, `mediainfo`…), jamais un chemin brut. Fournisseur actuel : l'outil de l'image. Le catalogue de l'éditeur (`media-tool-actions.ts`) est éditorial et n'atteste pas qu'un binaire est installé ; seul l'agent déclare outils, versions et encodeurs vérifiés.
- **Non implémenté** (conçu, jamais livré) : fournisseurs `custom`/`managed`, manifeste `tool.json`, dossier `/opt/encoding/tools/`, test de conformité. Principe à retenir s'il est repris : conserver toujours le FFmpeg de référence de l'image pour les sondes matérielles et le heartbeat. Voir [open-questions.md](../open-questions.md).

## Invariants
1. Un plugin ne contourne ni attribution, ni accès bibliothèque, ni baux, ni scan, ni validation/publication.
2. Descriptor disponible ≠ plugin exécutable sur un worker.
3. Jamais de secret ni de chemin local dans les sorties, journaux ou exports.
4. Le flow historique reste valide quand un plugin évolue.
