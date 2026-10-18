# 0064 — Outils média personnalisés, gérés et isolation des sondes de nœuds

Statut : décision d'architecture. Complète [0005](0005-media-tools-in-flow-components.md), [0015](0015-flow-runtime-and-portable-archives.md), [0027](0027-video-encoder-quality-profiles.md), [0036](0036-single-image-and-runtime-node-mode.md), [0054](0054-script-tools-helper.md) et [0063](0063-plugin-store-and-repositories.md).

---

## 1. Contexte & Problématique

Dans Encoding, les composants de flow s'appuient sur des outils média standards (FFmpeg, FFprobe, MKVToolNix, MediaInfo, ImageMagick) pour analyser, remuxer et transcoder les fichiers.

Les utilisateurs avancés souhaitent pouvoir :
1. Remplacer un outil existant par une variante ou une version personnalisée (ex. build `jellyfin-ffmpeg`, build FFmpeg compilé avec des patches spécifiques, ou version plus récente).
2. Ajouter de nouveaux outils spécialisés (ex. `whisper.cpp`, `handbrake-cli`, `av1an`, `flac`).

### Risques identifiés
- **Rupture des sondes matérielles et du heartbeat** : `ToolCapabilityProbe` exécute des commandes d'interrogation (`-hide_banner -encoders`) et teste des mires synthétiques GPU/CPU pour déclarer l'éligibilité du worker au cluster. Un binaire défaillant ou incompatible risquerait de paralyser la découverte du nœud.
- **Rupture de compatibilité avec les plugins** : un composant qui attend des arguments ou des formats de sortie précis (sorties JSON, regex de progression) échouerait si l'outil tiers ne respecte pas l'interface attendue.
- **Blocage par le bac à sable Landlock** : sous Linux, le worker restreint les accès disque. Un binaire placé hors des chemins autorisés sera bloqué à l'exécution.

---

## 2. Décisions d'Architecture

### Règle 1 : Séparation stricte entre « Rôle » et « Fournisseur » (Provider)
Les composants de flow et les plugins ne font jamais référence à des chemins d'exécutables bruts (comme `/usr/bin/ffmpeg`). Ils consomment un **Rôle d'outil canonique** (`ffmpeg`, `ffprobe`, `mkvmerge`, `mediainfo`, etc.).

Pour chaque rôle, un worker possède un fournisseur configuré :
- **Fournisseur Système / Référence (Stock)** : le binaire fourni par l'image Docker `encoding` (immuable, testé, zéro maintenance).
- **Fournisseur Personnalisé (Custom)** : chemin absolu renseigné par l'administrateur pointant vers un binaire local ou monté dans le conteneur.
- **Fournisseur Géré (Managed Package)** : binaire téléchargé depuis le Store ou un dépôt de packages d'outils.

### Règle 2 : Préservation de l'outil de référence pour les sondes de nœuds
Pour garantir que la découverte du matériel et la télémétrie du cluster restent infaillibles :
- **L'outil FFmpeg système par défaut est toujours conservé** dans l'image Docker.
- **Les sondes d'inventaire du nœud** (`ToolCapabilityProbe`, détection GPU `/dev/dri`, capacités du processeur) continuent de s'appuyer en priorité sur cette version de référence pour établir le profil matériel stable du worker.
- L'outil personnalisé est évalué séparément par une sonde de conformité dédiée sans jamais compromettre le signalement de base du nœud au cluster.

### Règle 3 : Le Contrat de Conformité (Barrière anti-casse)
Aucun exécutable personnalisé ne peut être activé pour l'exécution des flows sans avoir validé un **test de conformité automatisé** :

```
[Étape 1 : Vérification de version]
  Exécute `<custom_bin> -version`
  -> Vérifie la présence de la commande et une version minimale compatible (ex: FFmpeg >= 5.1).

[Étape 2 : Vérification des interfaces]
  Exécute `<custom_bin> -hide_banner -encoders`
  -> Vérifie que le format de sortie est conforme aux expressions régulières du parseur.

[Étape 3 : Mire synthétique (Smoke Test)]
  Encode 1 seconde de vidéo de test en mémoire/dossier temporaire (CPU et GPU si configuré).
  -> Vérifie que le binaire produit un conteneur intègre et s'exécute sans erreur de bibliothèque dynamique.
```

- **En cas de succès** : le binaire custom est marqué comme `Prêt` et devient utilisable par les flows.
- **En cas d'échec** : le binaire est rejeté avec un motif détaillé dans l'interface d'administration. Le worker continue d'utiliser automatiquement la version de référence par défaut.

---

## 3. Ajout de NOUVEAUX Outils (Manifeste `tool.json`)

Pour déclarer un outil externe non prévu nativement (ex. `whisper-cli`, `flac`, `av1an`), le système s'appuie sur un manifeste déclaratif d'outil :

```json
{
  "$schema": "https://encoding.io/schemas/v1/tool.json",
  "id": "whisper-cli",
  "name": "Whisper.cpp Transcription",
  "description": "Moteur d'inférence haute performance pour la transcription et le sous-titrage automatique.",
  "category": "ai-transcription",
  "executable": "whisper-cli",
  "probe": {
    "versionArgs": ["--version"],
    "versionRegex": "whisper\\.cpp\\s+v(?<version>[0-9\\.]+)",
    "smokeTestArgs": ["--help"]
  },
  "capabilitiesExposed": [
    "transcription",
    "cuda-acceleration"
  ]
}
```

### Intégration dynamique dans le cluster :
1. `ToolCapabilityProbe` charge dynamiquement les définitions de `tool.json` enregistrées sur le worker.
2. Pour chaque outil déclaré, il exécute la sonde configurée.
3. Si l'outil est détecté et validé, il est ajouté automatiquement à la liste `Capabilities` envoyée dans le heartbeat (`NodeToolCapability`).
4. Le planificateur (`RuntimeEligibility`) peut alors attribuer des étapes de flow exigeant cet outil uniquement aux workers qui l'ont validé.
5. Dans les scripts (`base-script`), l'aide `tools.run("whisper-cli", ...)` (architecture 0054) résout automatiquement le chemin de l'exécutable validé.

---

## 4. Sécurité et compatibilité Docker / Landlock

1. **Chemin d'installation standardisé** :
   - Tous les outils additionnels ou personnalisés doivent résider dans `/opt/encoding/tools/<tool-id>/` (ou y être montés via un volume Docker).
2. **Autorisations du bac à sable** :
   - Le profil Landlock (0015 & 0054) autorise nativement le dossier `/opt/encoding/tools/` en lecture et exécution.
   - Les outils personnalisés restent strictement confinés : aucun accès aux fichiers système sensibles, aux secrets de nœuds ou aux bibliothèques multimédias en dehors du dossier temporaire de travail de l'exécution courante.
3. **Compatibilité d'architecture** :
   - Le système vérifie que le binaire correspond à l'architecture hôte (`x86_64` / `arm64`) avant toute tentative d'exécution.

---

## 5. Granularité de Configuration (Nœud vs Flow)

L'utilisation d'un outil personnalisé s'applique selon deux niveaux de granularité :
1. **Niveau Nœud (Configuration par défaut)** :
   - L'administrateur du worker configure le fournisseur utilisé par défaut pour chaque rôle (`ffmpeg` -> Stock ou Custom).
2. **Niveau Flow / Bloc (Surcharge explicite)** :
   - Un composant de flow spécifique peut requérir une variante précise ou une capacité minimale (ex. `requiresTool: "ffmpeg", minVersion: "7.0"` ou `toolProvider: "jellyfin-ffmpeg"`).
   - L'éligibilité du worker garantit que le job ne sera confié qu'à un nœud disposant du fournisseur demandé ayant passé avec succès son test de conformité.
