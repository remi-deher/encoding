# 0068 — Blocs de condition HDR, Dolby Vision et profondeur de couleur

Statut : implémentée. Plugin Conditions 1.2.0.

## Contexte et besoin

Dans une médiathèque moderne, la distinction entre vidéos SDR, HDR standard (HDR10 / HLG) et HDR dynamique (Dolby Vision / HDR10+) est fondamentale :
1. **Protection des métadonnées dynamiques** : Le transcodage naïf d'une vidéo Dolby Vision (Profil 5, 7 ou 8) détruit souvent les métadonnées dynamiques RPU (Reference Processing Unit) ou produit une image verdâtre/violette (sur profil 5) ou délavée. De nombreux administrateurs souhaitent une règle explicite : « Si la vidéo est en Dolby Vision, ne pas la réencoder, ou l'aiguiller vers un traitement adapté ».
2. **Gestion de la profondeur de couleur (10-bit vs 8-bit)** : Les contenus 10-bit nécessitent un traitement spécifique pour préserver les dégradés et éviter les effets de bandes (banding).

## Décision

Mise à jour du plugin `conditions` en version **1.2.0** avec l'ajout de deux nouveaux composants de condition média dédiés :

### 1. `conditions-video-hdr` (« Format HDR / Dynamique »)
- **Objectif** : Vérifier si le flux vidéo principal utilise un format HDR spécifique ou SDR.
- **Paramètres déclaratifs** :
  - `format` (liste déroulante `inline`, défaut `any-hdr`) :
    - `any-hdr` : Tout format HDR (Dolby Vision, HDR10, HDR10+, HLG).
    - `dolby-vision` : Dolby Vision (Profils 5, 7, 8 via détection de la couche RPU / `DOVI configuration record`).
    - `hdr10` : HDR10 (espace colorimétrique BT.2020 et courbe de transfert SMPTE ST 2084 / PQ).
    - `hdr10plus` : HDR10+ (métadonnées dynamiques SMPTE 2094-40).
    - `hlg` : Hybrid Log-Gamma (courbe ARIB STD-B67).
    - `sdr` : Standard Dynamic Range (BT.709 classique / non HDR).
  - `invert` (booléen, défaut `false`) : « Inverser la condition (N'est pas) ».
- **Sorties** :
  - `yes` : « Oui ».
  - `no` : « Non ».
  - `error` : « Erreur » (aucune piste vidéo).

### 2. `conditions-video-bitdepth` (« Profondeur de couleur »)
- **Objectif** : Vérifier la profondeur de couleur de la vidéo (8-bit, 10-bit, 12-bit).
- **Paramètres déclaratifs** :
  - `depth` (liste déroulante `inline`, défaut `10-bit`) :
    - `10-bit` : 10 bits par composante (HEVC Main 10, AV1 Main, ProRes).
    - `8-bit` : 8 bits par composante (SDR standard).
    - `12-bit` : 12 bits par composante (Dolby Vision MEL/FEL étendu).
  - `invert` (booléen, défaut `false`) : « Inverser la condition (N'est pas) ».
- **Sorties** :
  - `yes` : « Oui ».
  - `no` : « Non ».
  - `error` : « Erreur » (aucune piste vidéo).

## Détection technique dans `MediaConditionEvaluator`

La détection s'appuie directement sur les métadonnées de `ffprobe` extraites lors du scan obligatoire initial (`EnsureScanAsync`) :
- **Dolby Vision** : inspection de `side_data_list` à la recherche de `DOVI configuration record` ou mentions de profil Dolby Vision.
- **HDR10 / HDR10+** : inspection de `color_transfer` (`smpte2084`), `color_primaries` (`bt2020`), et métadonnées de mastering (`Mastering display metadata`, `SMPTE 2094-40`).
- **HLG** : détection de `color_transfer` (`arib-std-b67` ou `hlg`).
- **Bit depth** : combinaison de `bits_per_raw_sample`, du format de pixel `pix_fmt` (`p010le`, `yuv420p10le`, etc.) et du profil vidéo (`Main 10`).
