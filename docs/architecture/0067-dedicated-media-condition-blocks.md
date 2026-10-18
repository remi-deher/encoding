# 0067 — Blocs de condition média dédiés et affichage direct

Statut : implémentée. Plugin Conditions 1.1.0.

## Contexte et besoin

Le plugin intégré `conditions` fournissait initialement le composant `conditions-match` (`0022`), permettant de construire des arbres booléens ET / OU imbriqués complexes. Bien que très puissant et expressif, ce bloc générique présente des limites d'usage au quotidien :
1. **Opacité visuelle sur le canevas de flow** : sur le diagramme, le bloc s'affiche uniquement sous le titre générique « Évaluer des conditions ». L'utilisateur ne peut pas savoir d'un coup d'œil ce que le bloc vérifie sans ouvrir le panneau de configuration latéral.
2. **Ergonomie des cas d'usage fréquents** : 90 % des tests de flow consistent à vérifier des caractéristiques fondamentales simples :
   - « La vidéo est-elle déjà en HEVC ? » (Oui ➔ continuer sans réencoder / Non ➔ réencoder)
   - « La piste audio est-elle en EAC3 ou AC3 ? »
   - « La vidéo est-elle en 1080p ou 4K ? »
   - « La piste audio a-t-elle au moins 6 canaux (5.1) ? »
   - « La langue française est-elle présente ? »

À l'instar d'outils comme FileFlows, disposer de blocs dédiés, dotés de contrôles directs sur la boîte ([`inline: true`](0011-flow-block-template.md)) et de sorties binaires explicites **Oui** (`yes`) et **Non** (`no`) apporte une clarté immédiate à la lecture du flow.

## Décision

Mise à jour du plugin `conditions` en version **1.1.0** avec l'ajout de 5 composants de condition média dédiés :

### 1. `conditions-video-codec` (« Codec vidéo »)
- **Objectif** : Vérifier le codec de la première piste vidéo du média.
- **Paramètres déclaratifs** :
  - `codec` (liste déroulante `inline`, défaut `hevc`) : `hevc`, `h264`, `av1`, `vp9`, `mpeg2video`, `vc1`.
  - `invert` (booléen, défaut `false`) : « Inverser la condition (N'est pas) ».
- **Sorties** :
  - `yes` : « Oui » (la vidéo correspond au codec ciblé).
  - `no` : « Non » (la vidéo utilise un autre codec).
  - `error` : « Erreur » (aucune piste vidéo présente).

### 2. `conditions-audio-codec` (« Codec audio »)
- **Objectif** : Vérifier le codec audio des pistes du média.
- **Paramètres déclaratifs** :
  - `codec` (liste déroulante `inline`, défaut `eac3`) : `eac3`, `ac3`, `aac`, `truehd`, `dts`, `dtshd`, `flac`, `mp3`, `opus`, `vorbis`.
  - `scope` (liste déroulante, défaut `any`) : « Au moins une piste » (`any`), « Première piste uniquement » (`first`), « Toutes les pistes » (`all`).
  - `invert` (booléen, défaut `false`) : « Inverser la condition (N'est pas) ».
- **Sorties** :
  - `yes` : « Oui ».
  - `no` : « Non ».
  - `error` : « Erreur » (aucune piste audio présente).

### 3. `conditions-video-resolution` (« Résolution vidéo »)
- **Objectif** : Vérifier la définition d'image du flux vidéo (avec détection intelligente des bandes noires et ratios cinématographiques).
- **Paramètres déclaratifs** :
  - `resolution` (liste déroulante `inline`, défaut `1080p`) : `4k` (UHD / 2160p), `1080p` (Full HD), `720p` (HD), `sd` (Standard Definition).
  - `operator` (liste déroulante, défaut `gte`) : « Au moins (≥) » (`gte`), « Exactement (=) » (`eq`), « Au plus (≤) » (`lte`).
- **Sorties** :
  - `yes` : « Oui ».
  - `no` : « Non ».
  - `error` : « Erreur » (aucune piste vidéo présente).

### 4. `conditions-audio-channels` (« Canaux audio »)
- **Objectif** : Vérifier le nombre de canaux audio (sur la première piste ou sur l'ensemble des pistes).
- **Paramètres déclaratifs** :
  - `channels` (liste déroulante `inline`, défaut `6`) : `8` (7.1), `6` (5.1), `2` (Stéréo), `1` (Mono).
  - `operator` (liste déroulante, défaut `gte`) : « Au moins (≥) » (`gte`), « Exactement (=) » (`eq`), « Au plus (≤) » (`lte`).
  - `scope` (liste déroulante, défaut `first`) : « Première piste uniquement » (`first`), « Au moins une piste » (`any`).
- **Sorties** :
  - `yes` : « Oui ».
  - `no` : « Non ».
  - `error` : « Erreur » (aucune piste audio présente).

### 5. `conditions-audio-language` (« Langue audio »)
- **Objectif** : Vérifier la présence d'une langue spécifique dans les pistes audio (avec normalisation automatique ISO 639-1 / 639-2).
- **Paramètres déclaratifs** :
  - `language` (liste déroulante `inline`, défaut `fra`) : `fra` (Français), `eng` (Anglais), `jpn` (Japonais), `deu` (Allemand), `spa` (Espagnol), `ita` (Italien), `por` (Portugais), `rus` (Russe), `zho` (Chinois), `kor` (Coréen), `und` (Indéterminée).
  - `scope` (liste déroulante, défaut `any`) : « Au moins une piste » (`any`), « Première piste uniquement » (`first`).
  - `invert` (booléen, défaut `false`) : « Inverser la condition (N'est pas) ».
- **Sorties** :
  - `yes` : « Oui ».
  - `no` : « Non ».
  - `error` : « Erreur » (aucune piste audio présente).

## Évaluation au runtime et simulation

1. **Scan automatique préalable** :
   Les blocs déclarent `requiresMedia: true`. Le runtime (`RuntimeFlowRunner.cs`) s'assure que le scan obligatoire d'entrée (`EnsureScanAsync()`) a été réalisé et que `CurrentMetadata` est disponible.
2. **Évaluateur partagé (`MediaConditionEvaluator`)** :
   Une classe statique partagée `MediaConditionEvaluator` dans `Encoding.Contracts.Flows.Conditions` implémente l'évaluation typée en mémoire sans invocation de sous-processus ni I/O supplémentaire :
   - Normalisation des alias de codecs (ex. `hevc` = `h265`, `h264` = `avc`, `eac3` = `e-ac-3` = `ec-3`, `dts` = `dca`).
   - Tolérance aux ratios cinématographiques (films 2.35:1 sans barres noires : 1920x800 reste correctement classifié en 1080p, 3840x1600 reste classifié en 4K).
   - Normalisation des codes de langue (ex. `fr`, `fre` normalisés en `fra`).
3. **Simulation et testeur de flow (`FlowSimulator`)** :
   - Si les métadonnées média sont présentes (profil de test ou import MediaInfo), les conditions sont évaluées immédiatement et de façon déterministe.
   - Si les métadonnées sont absentes (profil générique sans flux), la simulation demande explicitement à l'utilisateur de supposer le résultat Oui / Non de l'étape sans bloquer l'exploration.
4. **Coexistence avec `conditions-match`** :
   `conditions-match` demeure pleinement disponible pour les compositions booléennes complexes (arbres ET/OU multi-critères).
