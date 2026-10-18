# 0069 — Transcodage vidéo adaptatif, paliers de qualité et préservation HDR

Statut : implémentée. Plugin Base 1.5.0.

## Contexte et besoin

Le bloc `base-transcode-video` (`0005`, `0011`) gérait le transcodage FFmpeg selon un profil de qualité (`EncodingProfileSnapshot`) ou une valeur statique brute de qualité (CRF/CQ). Cette approche présentait trois limitations techniques majeures dans le traitement automatisé de bibliothèques multimédias :
1. **Absence d'adaptation à la résolution** :
   Appliquer un même CRF fixe à de la 4K (3840×2160) et à de la SD/720p produit des résultats sous-optimaux. En 4K, un CRF plus élevé (+2 à +3) préserve une netteté remarquable tout en évitant l'explosion du débit causée par le grain de pellicule. À l'inverse, en 720p ou SD, un CRF plus strict est nécessaire pour éviter que l'image ne devienne floue.
2. **Effet de banding sur les dégradés 8-bit** :
   Pour les codecs modernes (HEVC et AV1), encoder en 10 bits (`yuv420p10le` ou `p010le`) est recommandé même lorsque la source est en 8 bits. Cela élimine les artefacts de dégradé par paliers (banding) dans les scènes sombres ou le ciel, tout en améliorant l'efficacité du compresseur.
3. **Altération des couleurs HDR / Dolby Vision (image délavée)** :
   Lorsqu'une vidéo HDR10, HLG ou Dolby Vision est réencodée sans réinjection explicite des paramètres d'espace colorimétrique (`-color_primaries`, `-color_trc`, `-colorspace`) et des paramètres du compresseur (`-x265-params hdr10=1:repeat-headers=1`), le diffuseur (téléviseur) ne reconnaît plus le flux HDR et affiche une image terne et délavée.

## Décision

Enrichissement du composant `base-transcode-video` dans le catalogue `base` (version 1.5.0) et dans la politique de transcodage `MediaCodecPolicy` :

### 1. Paliers de qualité adaptatifs (`qualityTier`)
Nouveau paramètre de sélection directe sur le bloc :
- `custom` : Utilise le profil de qualité configuré ou la valeur numérique manuelle.
- `transparent` : Visuellement transparent / qualité archivage (CRF 18 en 1080p, CRF 21-22 en 4K).
- `high` : Haute qualité recommandée (CRF 21 en 1080p, CRF 24 en 4K, CQ 23 en NVENC).
- `balanced` : Équilibré (CRF 23 en 1080p, CRF 26 en 4K, CQ 25 en NVENC).
- `compact` : Compacité maximale / faible débit (CRF 26 en 1080p, CRF 29 en 4K, CQ 28 en NVENC).

La méthode `MediaCodecPolicy.ResolveQuality` calcule dynamiquement la valeur optimale selon :
- L'accélérateur et l'encodeur retenu (CPU `libx265`, `libx264`, `hevc_nvenc`, `hevc_qsv`, `libsvtav1`).
- La définition réelle du média scanné (4K, 1080p, 720p, SD).

### 2. Gestion de la profondeur de couleur (`colorDepth`)
- `auto` (défaut) : Sélectionne automatiquement le 10-bit (`p010le` sur NVENC/QSV, `yuv420p10le` sur CPU) pour HEVC et AV1, et conserve le 8-bit pour H.264 standard.
- `10-bit` : Force l'encodage en 10 bits (Main 10).
- `source` : Conserve strictement la profondeur de couleur de la source.
- `8-bit` : Force l'encodage en 8 bits.

### 3. Préservation automatique des métadonnées HDR (`preserveHdr`)
Activée par défaut (`true`) :
- Détecte les balises colorimétriques de la source (`color_primaries`, `color_trc`, `color_space`).
- Transmet les arguments appropriés à FFmpeg (`-color_primaries`, `-color_trc`, `-colorspace`).
- Pour `libx265`, injecte `-x265-params hdr10=1:hdr10-opt=1:repeat-headers=1` lorsque la courbe de transfert SMPTE ST 2084 (PQ) est détectée, garantissant une restitution parfaite sur les téléviseurs HDR.
