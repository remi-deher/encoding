# 0070 — Blocs de traitement inspirés de FileFlows

Statut : validé et implémenté.

## Contexte

Dans les chaînes d’automatisation multimédia (Plex, Jellyfin) inspirées d’outils comme FileFlows, Tdarr ou Unmanic, plusieurs étapes de filtrage, d'optimisation géométrique et de contrôle de qualité sont indispensables avant et après le transcodage vidéo :
1. Détecter et supprimer les bandes noires (letterboxing/pillarboxing) pour concentrer le débit binaire sur l'image active sans gaspillage CPU/GPU.
2. Détecter et désentrelacer les sources entrelacées (télévision 1080i, DVD) tout en préservant intactes les sources progressives.
3. Filtrer les pistes audio et sous-titres selon une liste de langues autorisées pour désencombrer les fichiers contenant des dizaines de doublages inutiles.
4. Supprimer les pistes de commentaires et d’audiodescription non sollicitées.
5. Vérifier le gain de taille après réencodage pour interdire le remplacement si le fichier n'a pas réduit ou s'il a grossi (phénomène fréquent sur des sources déjà compressées ou bruitées).

## Décision

Ajouter dans le plugin `base` (version 1.6.0) 5 blocs de traitement dédiés et optimisés, intégrables directement au plan d’encodage FFmpeg (`ProcessingPlan`) ou exécutables individuellement.

### 1. `base-auto-crop` — Détection et rognage des bandes noires
- **Fonctionnement** :
  - Échantillonne 5 points temporels dans le fichier (10%, 25%, 50%, 75%, 90% de la durée) avec le filtre `cropdetect=24:16:0` de FFmpeg sur 2 secondes d'échantillon.
  - Extrait les boîtes `crop=W:H:X:Y` et calcule la boîte consensuelle médiane afin d'éviter les faux positifs liés aux scènes sombres ou aux génériques.
  - Normalise les dimensions $W$ et $H$ sur des multiples de 2 (ou 16) pour la compatibilité avec les encodeurs vidéo.
  - Si un rognage significatif est détecté ($W < \text{Largeur} - 16$ ou $H < \text{Hauteur} - 16$) :
    - Mode `plan` (par défaut) : ajoute le filtre `crop=W:H:X:Y` à `videoFilters` pour être exécuté en **une passe unique** lors de `base-transcode-video` sans fichier intermédiaire.
    - Expose les variables de flow : `video.crop.w`, `video.crop.h`, `video.crop.x`, `video.crop.y` et `video.crop.filter`.
- **Sorties** :
  - `cropped` : Bandes noires détectées et rognage configuré/appliqué.
  - `no-crop` : Vidéo plein cadre (aucune bande noire significative).
  - `error` : Échec de la sonde de détection.

### 2. `base-file-size-guard` — Garde de réduction de taille
- **Fonctionnement** :
  - Mesure la taille de la copie de travail actuelle et la compare à la source d'origine ou à l'étape précédente.
  - Paramètre `minReductionPercent` : seuil minimum de gain en pourcentage (ex: `10`% d'économie requise).
  - Paramètre `actionOnFailure` :
    - `branch` : emprunte la branche alternative `larger`.
    - `revert` : restaure le fichier source initial dans la copie de travail pour annuler l'encodage inefficace et emprunte `larger`.
- **Sorties** :
  - `reduced` : Le fichier s'est allégé d'au moins le seuil demandé.
  - `larger` : Gain insuffisant ou fichier encodé plus volumineux.
  - `error` : Fichier introuvable ou erreur de lecture.

### 3. `base-filter-languages` — Filtrage des langues audio et sous-titres
- **Fonctionnement** :
  - Filtre les pistes selon les codes ISO-639-2 autorisés (ex: `fra,fre,eng,jpn,und`).
  - Garde-fous intégrés :
    - `keepUndefinedAudio` : conserve les pistes audio sans langue spécifiée.
    - `keepFirstAudioIfNoneMatch` : conserve impérativement la première piste audio si aucune ne correspond à la sélection (évite tout fichier muet).
    - `allowedSubtitleLanguages` : liste des langues de sous-titres conservées.
    - `keepForcedSubtitles` : conserve toujours les sous-titres forcés.
  - S'applique sur conteneur Matroska via `mkvmerge` ou dans le plan de remuxage.
- **Sorties** :
  - `filtered` : Des pistes hors liste ont été retirées.
  - `unchanged` : Toutes les pistes étaient déjà conformes.
  - `error` : Erreur de traitement ou conteneur incompatible.

### 4. `base-deinterlace-video` — Détection et désentrelacement automatique
- **Fonctionnement** :
  - Analyse 1000 trames avec le filtre FFmpeg `idet` (`Multi frame detection`).
  - Calcule le ratio de trames entrelacées ($(\text{TFF} + \text{BFF}) / \text{Total}$).
  - Si le ratio dépasse le seuil paramétré (défaut : 10%) :
    - Injecte le filtre `bwdif` (ou `yadif`) dans `videoFilters` pour le plan d'encodage `base-transcode-video`.
- **Sorties** :
  - `deinterlaced` : Contenu entrelacé détecté, filtre ajouté au plan.
  - `progressive` : Contenu progressif, aucune modification.
  - `error` : Échec de la sonde.

### 5. `base-remove-commentary` — Nettoyage commentaires et audiodescription
- **Fonctionnement** :
  - Scanne les titres, noms et drapeaux des pistes audio et sous-titres.
  - Recherche les motifs : `commentary`, `commentaires`, `director`, `audio description`, `audiodescription`, `visual impaired`, `dvs`.
  - Retire les pistes correspondantes via `mkvmerge` ou le plan.
- **Sorties** :
  - `removed` : Pistes indésirables détectées et supprimées.
  - `unchanged` : Aucune piste de commentaire trouvée.
  - `error` : Erreur de traitement.

## Conséquences

- Les flows peuvent reproduire fidèlement les pipelines FileFlows sans scripts ad-hoc complexes.
- L'intégration de `base-auto-crop` et `base-deinterlace-video` au plan FFmpeg évite les passes d'encodage multiples et réduit considérablement le temps global de traitement.
- La sécurité d'intégrité est renforcée par `base-file-size-guard` qui protège la bibliothèque contre les réencodages contre-productifs.
