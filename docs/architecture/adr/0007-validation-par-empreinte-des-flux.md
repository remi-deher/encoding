---
id: 0007
title: Décodage complet seulement quand les flux audio/vidéo changent (empreinte des paquets)
status: proposed
domain: runtime
date: 2026-10-23
decided_by: à décider (Rémi)
supersedes: []
superseded_by: []
tags: [validation, decodage, performance, integrite, checkpoint]
---

# 0007 — Décodage complet seulement quand les flux audio/vidéo changent

## Contexte
`RuntimeFlowRunner.ApplyOutcomeAsync` appelle `MediaScanner.ValidateAsync(candidate, …, decode: true)` après **chaque** étape qui produit ou modifie le fichier courant (y compris le rattrapage de fin d'étape quand un script a réécrit `current`, ex. `mkvpropedit`). Chaque appel lance `ffmpeg -v error -xerror -i … -map 0:v? -map 0:a? -f null -`, un décodage complet. Mesuré sur l'exécution `33e03b0a` (30 s de HEVC Dolby Vision) : 4 à 5 décodages de 7 à 9 s chacun, dont trois sur des flux **inchangés** (remux des sous-titres, ajout de polices, `mkvpropedit`). Sur un film de 2 h, des dizaines de minutes de CPU par fichier.

Le décodage protège un seul risque : un flux audio/vidéo **illisible** (corruption, encodeur défaillant, copie tronquée). Il ne protège ni les pistes de sous-titres, ni les pièces jointes, ni les en-têtes (ces contrôles sont dans `ffprobe` : pistes, type attendu, durée). Hors transfert (`decode:false` déjà), la validation avant publication s'appuie aujourd'hui **uniquement** sur le dernier décodage de `ApplyOutcomeAsync` : `base-publish-result` ne décode pas.

## Décision
1. **Preuve par empreinte.** Le contexte d'exécution (`RuntimeContext`, donc checkpoint et relais) mémorise `AvFingerprint` (empreinte SHA-256 par flux audio/vidéo des paquets, via `ffmpeg -map 0:v? -map 0:a? -c copy -f streamhash -hash sha256 -`) et `ValidatedSha256` (SHA-256 du fichier dont les flux A/V ont été décodés sans erreur avec cette empreinte). Champs optionnels, absents des anciens checkpoints.
2. **Règle dans `ApplyOutcomeAsync`** (et rattrapage de fin d'étape) : contrôle léger toujours (`ffprobe` : fichier non vide, média attendu, durée cohérente avec la source). Puis, selon l'empreinte du candidat :
   - **identique** à `AvFingerprint` : les paquets A/V sont les mêmes octets qu'un résultat déjà décodé sans erreur ; pas de nouveau décodage (remux, sous-titres, polices, `mkvpropedit`, fichier inchangé) ; `ValidatedSha256` prend le SHA-256 du candidat ;
   - **différente** ou **sans référence** (première écriture, réencodage vidéo ou audio, flux retirés ou réordonnés, conversion de conteneur qui modifie les paquets) : décodage complet, avec calcul de l'empreinte **dans la même exécution de ffmpeg** (deux sorties, une lecture) ; succès → `AvFingerprint` et `ValidatedSha256` mis à jour.
   La règle se fonde sur ce qui est **prouvé** sur le fichier, jamais sur ce que le bloc ou le script déclare : un script qui corrompt un flux en déclarant une modification d'en-tête est détecté (empreinte différente → décodage).
3. **Garde avant publication.** `base-publish-result` refuse de demander la publication si `ValidatedSha256 ≠ CurrentSha256` : il lance alors le décodage complet lui-même (cas d'un checkpoint ancien, d'une reprise après perte du worker sans preuve, d'un fichier restauré). Un fichier publié a donc toujours des flux A/V décodés sans erreur ou identiques à des flux qui l'ont été.
4. **Rattrapage de secours** : `ENCODING_RUNTIME_ALWAYS_DECODE=1` (option du nœud, pas de réglage d'interface) rétablit l'ancien comportement : décodage après chaque étape.
5. Le transfert vers le nœud d'accès (`RuntimeTransferService`) garde son contrôle sans décodage (déjà le cas) ; le SHA-256 vérifié sur le transfert prouve qu'il s'agit du fichier validé.

## Alternatives écartées
- **Règle déclarative par bloc** (« ce bloc ne réencode pas ») : les scripts (`candidate`) et les plugins n'ont pas de déclaration fiable ; un bloc mal classé laisserait passer une corruption.
- **Un seul décodage, juste avant publication** : plus simple mais une étape qui échoue tard fait perdre tout le traitement, et le diagnostic ne désigne plus l'étape fautive. Reste possible comme option si Rémi préfère l'économie maximale.
- **Aucune empreinte, décodage seulement après réencodage connu** : reposerait sur la classification des blocs (voir plus haut).
- **Empreinte des seuls en-têtes ffprobe** : ne détecte pas une corruption des données.

## Conséquences et risques
- Coût restant : une lecture + démultiplexage (sans décodage) par étape qui écrit un fichier ; limité par les E/S, de l'ordre de la seconde pour 30 s de vidéo locale, mais une lecture complète du fichier à chaque étape sur stockage réseau. Une optimisation (étape connue comme en-tête seulement) reste possible plus tard.
- Pas de décodage du **résultat de remux** : une erreur du muxeur qui préserve les paquets mais casse le conteneur est vue par `ffprobe`/l'empreinte (`-xerror`), pas par un décodage. Une corruption introduite hors de ffmpeg dans l'index (cues) n'est pas vue, comme avant (le décodage `-f null` ne lit pas les cues).
- Conversion de conteneur qui transforme les paquets (ex. TS → MKV) : empreinte différente, donc décodage ; sûr, pas de gain.
- Compatibilité cluster : champs optionnels du checkpoint, ignorés par un ancien serveur (le checkpoint les perd : le nœud suivant décode, plus lent mais sûr) ; pas de migration. Un ancien worker continue de décoder à chaque étape.
- Interaction avec ADR 0004 : après reprise, la preuve voyage dans le checkpoint ; sans preuve (checkpoint ancien) la garde de l'article 3 décode.
- Tests prévus (`TEST_RULES.md`) : pas de décodage après étape d'en-tête ni remux ; décodage après réencodage ; décodage final avant publication si la preuve manque ; corruption introduite par une étape intermédiaire détectée avant publication ; reprise sans preuve ne saute pas la validation ; `ALWAYS_DECODE`.
- Non vérifié en réel : gains sur .50/.51 et stockage réseau (mesure prévue dans le bac à sable `SANDBOX_V6_vs_V3_20261023`, après accord).
