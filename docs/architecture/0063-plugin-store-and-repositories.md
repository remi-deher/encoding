# 0063 — Store de plugins, dépôts décentralisés et gestion des paquets ZIP

Statut : décision d'architecture, implémentée en grande partie (voir « État d'implémentation » en fin de document). Complète [0011](0011-flow-block-template.md), [0013](0013-flow-catalog-and-validation.md), [0019](0019-plugin-configuration.md), [0032](0032-integration-plugins-and-sdk.md), [0049](0049-multi-server-shared-cluster.md) et [0062](0062-plugin-and-component-authoring-for-ai-and-mcp.md).

---

## 1. Contexte & Objectifs

L'écosystème Encoding permet d'étendre les capacités des flows et des intégrations via des plugins exécutés en processus séparés sous bac à sable Landlock (Python 3, conformément à 0032).

Pour permettre aux utilisateurs de découvrir, d'installer et de créer des extensions sans modifier le cœur de l'application, nous introduisons un mécanisme de **Store de plugins** basé sur des **dépôts décentralisés et statiques**, inspiré des modèles de paquets modernes (HACS, Flathub, Helm).

### Principes directeurs validés
1. **Dépôt officiel par défaut** : embarqué en dur dans l'application (`https://plugins.encoding.io/v1/repository.json` ou équivalent officiel), actif à l'installation.
2. **Dépôts tiers configurables** : possibilité pour les administrateurs d'ajouter, modifier ou désactiver d'autres sources d'index via de simples URLs HTTP/HTTPS.
3. **Format d'archive `.zip`** : paquet standardisé contenant le manifeste déclaratif, le code Python et les assets éventuels.
4. **Namespace strict** : tout plugin possède un identifiant qualifié (ex. `official.plex`, `community.whisper`) interdisant toute usurpation ou écrasement silencieux d'un identifiant officiel par un dépôt tiers.
5. **Mises à jour manuelles** : l'application signale la disponibilité d'une nouvelle version (badge, changelog, notifications) mais ne met jamais à jour un composant automatiquement afin de préserver la stabilité des exécutions de flow.
6. **Sideloading manuel** : possibilité d'importer directement une archive `.zip` depuis l'interface ou l'API sans passer par un dépôt (développement local, environnements isolés/air-gap).

---

## 2. Contrat de Dépôt (`repository.json`)

Un dépôt est un simple point de distribution statique (hébergeable sur GitHub Pages, un CDN, un bucket S3 ou un serveur Nginx local).

### Structure du manifeste de dépôt

```json
{
  "$schema": "https://encoding.io/schemas/v1/repository.json",
  "id": "community-transcode",
  "name": "Dépôt Communautaire Transcodage",
  "description": "Plugins de transcodage avancés et filtres expérimentaux.",
  "url": "https://plugins.example.com/encoding/repository.json",
  "maintainer": {
    "name": "John Doe",
    "contact": "https://github.com/example/plugins"
  },
  "schemaVersion": "1.0.0",
  "updatedAt": "2026-10-18T00:00:00Z",
  "categories": [
    { "id": "video", "name": "Traitement Vidéo", "description": "Filtres, transcodeurs et analyseurs vidéo." },
    { "id": "audio", "name": "Traitement Audio", "description": "Gestion des pistes, normalisation sonore." },
    { "id": "subtitles", "name": "Sous-titres", "description": "Extraction, OCR, conversion et styles." },
    { "id": "integrations", "name": "Services Externes", "description": "Plex, Jellyfin, Sonarr, Radarr, etc." },
    { "id": "utilities", "name": "Utilitaires", "description": "Outils généraux, webhooks et alertes." }
  ],
  "plugins": [
    {
      "id": "example.whisper-subtitles",
      "name": "Whisper Subtitles Generator",
      "summary": "Génère des sous-titres automatiques via Whisper et les injecte dans le conteneur MKV.",
      "category": "subtitles",
      "iconUrl": "https://plugins.example.com/encoding/icons/whisper.png",
      "latestVersion": "1.2.0",
      "versions": [
        {
          "version": "1.2.0",
          "minEngineVersion": "1.0.0",
          "downloadUrl": "https://plugins.example.com/encoding/packages/whisper-subtitles-1.2.0.zip",
          "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
          "releaseDate": "2026-10-15T12:00:00Z",
          "changelog": "Ajout du modèle medium pour une meilleure précision en français."
        }
      ]
    }
  ]
}
```

### Règles d'un dépôt conforme
- **Immuabilité des versions publiées** : le fichier zip pointé par une version donnée (`1.2.0`) et son hash SHA-256 ne doivent jamais être modifiés après publication. Toute correction exige une incrémentation de version (SemVer).
- **Contrôle d'intégrité** : le champ `sha256` est obligatoire pour chaque archive. L'agent de téléchargement d'Encoding rejette toute archive dont l'empreinte calculée ne correspond pas à celle annoncée.

---

## 3. Contrat d'un Paquet Plugin (`.zip`)

Une archive `.zip` de plugin valide doit respecter une arborescence stricte et contenir au minimum le fichier `plugin.json` à sa racine.

### Arborescence type du `.zip`

```
plugin-package.zip
├── plugin.json                 # Manifeste déclaratif obligatoire (composants, schémas)
├── README.md                   # Documentation utilisateur (rendue dans la fiche du store)
├── LICENSE                     # Licence du code
├── icon.png                    # Icône du plugin (optionnel, 256x256 recommandé)
├── main.py                     # Point d'entrée exécutable (protocole SDK Python 0032)
└── lib/                        # Modules et helpers Python autonomes (sans packages C natifs non autorisés)
```

### Structure du manifeste `plugin.json`

Le manifeste encapsule l'identité du plugin, ses exigences système et la déclaration de ses blocs de flow (selon 0011, 0019 et 0062) :

```json
{
  "$schema": "https://encoding.io/schemas/v1/plugin.json",
  "id": "community.whisper-subtitles",
  "version": "1.2.0",
  "name": "Whisper Subtitles Generator",
  "description": "Génère des sous-titres automatiques via Whisper et les injecte dans le conteneur MKV.",
  "author": "Community Contributors",
  "homepage": "https://github.com/example/whisper-subtitles",
  "license": "MIT",
  "runtime": {
    "type": "python3",
    "entrypoint": "main.py"
  },
  "compatibility": {
    "minEngineVersion": "1.0.0",
    "requiredTools": ["mkvmerge", "ffmpeg"]
  },
  "globalConfigurationSchema": {
    "type": "object",
    "properties": {
      "defaultModel": {
        "type": "string",
        "title": "Modèle par défaut",
        "description": "Taille du modèle Whisper utilisé en l'absence de surcharge dans le flow.",
        "default": "small",
        "enum": ["tiny", "base", "small", "medium"]
      }
    }
  },
  "components": [
    {
      "id": "generate-subtitles",
      "name": "Générer les sous-titres (Whisper)",
      "description": "Extrait l'audio de la piste sélectionnée, exécute la transcription et produit un fichier SRT/ASS temporaire.",
      "kind": "action",
      "outputs": [
        { "id": "output", "label": "Succès", "description": "Sous-titres générés et synchronisés." },
        { "id": "noAudio", "label": "Pas d'audio éligible", "description": "Aucune piste audio dans la langue ciblée." },
        { "id": "error", "label": "Erreur", "description": "Échec de transcription ou outil manquant." }
      ],
      "configurationSchema": { ... }
    }
  ]
}
```

---

## 4. Politique de Nommage (Namespace Strict)

Pour éviter toute collision, conflit de résolution ou attaque par substitution de nom (typosquatting ou écrasement de composants officiels) :

1. **Format d'identifiant obligatoire** : `<namespace>.<plugin-name>` (composé uniquement de minuscules, chiffres, tirets et un préfixe de namespace).
2. **Réservation du namespace `official.*`** : seul le dépôt officiel configuré en dur peut distribuer des plugins débutant par `official.` (ex. `official.plex`, `official.sonarr`). Un dépôt tiers proposant un ID en `official.` est rejeté lors de la synchronisation de son index.
3. **Isolation complète** : deux dépôts tiers peuvent proposer des plugins similaires à condition que leurs namespaces diffèrent (`myorg.transcode` vs `otherorg.transcode`).

---

## 5. Prérequis Image Docker & Runtime

Pour manipuler les archives de plugins :
1. **Outils d'extraction dans l'image unique Docker (`encoding`)** :
   - L'exécutable `unzip` / utilitaires d'archives standard Linux doivent être installés dans l'image de base (ou décompression gérée nativement via `System.IO.Compression` dans le runtime ASP.NET Core).
   - Préservation des permissions d'exécution (`chmod +x` sur le script d'entrée `main.py` lors de l'extraction).
2. **Isolation Landlock & Sécurité** :
   - L'archive est extraite dans un répertoire dédié en lecture seule pour le worker (`/opt/encoding/custom-plugins/<plugin-id>/<version>/`).
   - Le script Python s'exécute avec les mêmes restrictions strictes que les plugins intégrés (0015 et 0032) : aucun accès au réseau local ou aux fichiers hors du répertoire temporaire attribué à la tâche courante.

---

## 6. Cycle de Vie dans un Cluster Multi-Serveurs (0049)

Dans une architecture distribuée, l'installation d'un plugin doit être cohérente sur l'ensemble des nœuds :

1. **Installation sur le Serveur** :
   - L'administrateur valide le téléchargement ou importe le fichier `.zip`.
   - Le serveur calcule le hash SHA-256, décompresse et valide le manifeste (`plugin.json`) contre le schéma JSON officiel.
   - Si la validation réussit, l'archive originale et les métadonnées sont stockées dans le dossier persistant partagé du cluster (`/data/plugins/store/...`).
   - Le registre de cluster (réconciliation 0049) enregistre l'état du plugin : `{ id, version, enabled: true, source: "repo:community-transcode" }`.
2. **Propagation aux Workers** :
   - Chaque worker surveille les plugins actifs déclarés par le cluster.
   - S'il ne dispose pas de la version locale, il récupère l'archive validée auprès du stockage local / pair serveur, l'extrait dans son espace de plugins et annonce la capacité correspondante (`plugin/<namespace.id>`).
3. **Mises à jour manuelles** :
   - Le serveur interroge périodiquement les dépôts (ex. toutes les 24h ou à la demande via `Synchroniser`).
   - S'il détecte une version supérieure (`1.3.0` disponible contre `1.2.0` installée), il positionne un indicateur `hasUpdate: true` dans l'API du Store.
   - Aucun fichier n'est remplacé automatiquement. L'administrateur clique sur `Mettre à jour` quand il le souhaite.

---

## 7. Interfaces Administrateur & Expérience Utilisateur

L'interface web Angular s'enrichit d'une section dédiée :
- **Paramètres > Dépôts** :
  - Liste des dépôts actifs avec statut (Dernière vérification, nombre de paquets).
  - Dépôt officiel verrouillé par défaut avec switch pour le désactiver si souhaité.
  - Formulaire d'ajout d'un dépôt (URL, validation du format immédiate).
  - Bouton `Importer une archive (.zip)` pour l'installation manuelle en direct.
- **Store de Plugins** :
  - Navigation par catégorie, recherche textuelle, tri par popularité ou date.
  - Carte de plugin : badge du namespace, description, icône, compatibilité avec la version actuelle du serveur.
  - Fiche détaillée : description longue, changelog, liste des blocs de flow ajoutés.
  - Bouton `Installer`, `Désactiver`, `Mettre à jour` (avec modal listant le changelog).

---

## 8. État d'implémentation (audit des plugins, 2026-10-21)

Les routes du Store renvoyaient auparavant des données factices ; elles sont maintenant réelles. Ce qui existe :

- **Paquet** (`Encoding.Core/Plugins/PluginPackage`) : `plugin.json` à la racine est un `PluginManifest` (le même contrat que les manifestes intégrés, voir 0062) complété d'un bloc `runtime` (`{ "type": "python3" | "node", "entrypoint": "main.py" }`). Le schéma `globalConfigurationSchema` en JSON Schema de l'exemple de la section 3 n'est **pas** retenu : un seul format de configuration existe (`configurationSchema`, 0019). Limites : 32 Mo compressé, 128 Mo décompressé, 500 fichiers ; tout chemin absolu, avec `..` ou `:` est refusé (zip slip). Identifiant qualifié `espace.nom` obligatoire.
- **Installation sur un serveur** (`PluginStoreService`) : empreinte SHA-256, enregistrement à chaud dans `FlowComponentRegistry.TryInstall` (sans redémarrage), stockage dans `PluginStore:Directory` (par défaut `<données du nœud>/plugin-store`), rechargement au démarrage (un paquet altéré sur disque est signalé dans la page Plugins et non chargé). Une version publiée est immuable ; une mise à jour exige une version supérieure et garde les composants de l'ancienne version résolubles par les flows existants (leurs références pointent vers le descripteur de la nouvelle version, qui doit rester compatible avec son schéma). `official.*` est refusé à l'import manuel et aux dépôts tiers.
- **Dépôts** (`PluginRepositoryService`) : `repository.json` 1.x, validé entrée par entrée (identifiant qualifié, empreinte de 64 hexadécimaux, URL HTTP(S) sans identifiants, version la plus récente présente). Le **dépôt officiel n'existe que si `PluginStore:OfficialRepositoryUrl` est configuré** : l'adresse `plugins.encoding.io` de la section 1 est fictive et n'est plus utilisée. L'archive téléchargée est rejetée si son SHA-256 diffère de celui de l'index.
- **Workers** (`PluginSyncService`, `CustomPluginCatalog`) : chaque minute, un worker liste les paquets du serveur (`GET /api/v1/store/installed`, jeton de nœud), télécharge ceux qui manquent, contrôle l'empreinte, extrait dans `Runtime:CustomPluginDirectory` (`/opt/encoding/custom-plugins` dans l'image) puis écrit un marqueur `.verified` en dernier. Le plugin est lancé sous Landlock avec l'interpréteur de son `runtime` et son seul dossier en lecture. Une panne réseau ne retire rien ; une version que le serveur ne propose plus est effacée. La capacité `plugin/<id>` n'est annoncée qu'après une poignée de main réussie, avec l'empreinte du contrat (comparée au catalogue du serveur).

Non implémenté, à décider ou à faire :

- **Pas de réplication entre serveurs d'un cluster** : l'état du Store est un dossier local ; un paquet doit être installé sur chaque serveur (0049 ne prévoit pas de blobs). Les flows qui utilisent un plugin absent d'un serveur y sont invalides.
- Mises à jour : l'interface propose « Mettre à jour » après synchronisation d'un dépôt ; il n'y a pas de synchronisation périodique automatique toutes les 24 h.
- Pas de désactivation d'un plugin sans le retirer, pas de refus de retrait quand un flow l'utilise, pas de signature des paquets (seule l'empreinte annoncée par le dépôt protège l'intégrité, et le dépôt est de confiance).
- `compatibility.minEngineVersion` et `requiredTools` ne sont pas contrôlés à l'installation.
- Rien n'a été exécuté sous Landlock réel (ni plugin de paquet, ni synchronisation worker de bout en bout) : les tests couvrent le paquet, l'installation, les dépôts contre un serveur HTTP local et la découverte côté worker.
