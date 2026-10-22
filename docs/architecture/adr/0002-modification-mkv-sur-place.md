---
id: 0002
title: Modification MKV sur place par bloc dédié
status: proposed
domain: runtime
date: 2026-10-22
decided_by: Rémi (2026-10-22) pour le principe « pas de politique de remplacement, bloc dédié ou case à cocher » ; détails à valider
supersedes: []
superseded_by: []
tags: [mkvpropedit, sur-place, intégrité, bail, verrou, publication, reprise]
---

# 0002 — Modification MKV sur place par bloc dédié

## Contexte
Les blocs qui appellent `mkvpropedit` (`mkv-edit-track-properties`, `mkv-edit-matroska-metadata`, `mkv-remove-attachments`, `mkv-replace-chapters`, `subtitles-name-tracks`) travaillent sur la **copie de travail**, puis le résultat est publié sans écrasement dans `.encoding-results/` ([runtime](../domains/runtime.md)). Pour un simple changement de métadonnées (titre, langue, drapeaux de piste), cela impose de copier et republier tout le fichier, alors que `mkvpropedit` ne réécrit normalement que les en-têtes. La modification sur place n'existe pas ([fileflows-v3-flow](../../operations/fileflows-v3-flow.md)). Le remplacement de la source n'est possible que par la politique de bibliothèque (invariant 4) : Rémi ne veut pas l'étendre.

## Décision
- Un **bloc dédié** `mkv-edit-in-place` (plugin `mkv`, version mineure suivante) modifie la source **sur place**. Les blocs existants restent inchangés (copie de travail). Une case « sur place » sur certains blocs existants reste possible plus tard avec le même moteur ; elle n'est pas livrée d'emblée, pour ne pas changer la sémantique de flows existants.
- Opérations limitées à ce que `mkvpropedit` fait réellement sur place : propriétés de piste, titre du segment, drapeaux. Chapitres et pièces jointes (qui peuvent déplacer des données) sont exclus tant que non validés sur fichiers réels.
- Aucune politique de remplacement ni période de grâce : l'opération est explicite dans le flow. L'invariant 4 est précisé : « jamais de **remplacement** depuis un flow seul » ; la modification d'en-têtes sur place par un bloc explicite est un second cas, régi par cet ADR.
- Exécution **uniquement sur le nœud d'accès en écriture directe** (`nodes-require-direct-access`), sous `base-disk-lock`, avec bail et époque vérifiés avant et après. Perte du bail = aucune modification démarrée.
- Protocole : (1) empreinte SHA-256, taille et date de la source ; (2) sauvegarde de l'état d'origine dans `.encoding-replaced/<exécution>/` ; (3) **intention durable** (checkpoint, même transaction) avant d'écrire ; (4) `mkvpropedit` ; (5) vérification : `mkvmerge -J` + `probe-validate-output` (pistes, durée, décodage) et propriétés attendues ; (6) intention marquée terminée. Échec à l'étape 4 ou 5 = restauration depuis la sauvegarde, puis échec de l'exécution.
- **Reprise** : intention sans fin après perte de nœud ou de bail → comparer l'empreinte courante à celle d'origine et à celle attendue ; restaurer ou terminer, jamais rejouer à l'aveugle. État indéterminé = échec visible, source conservée dans la sauvegarde.
- Garde de publication (`plex-check-playback`) appliquée avant la modification : un fichier en lecture n'est pas modifié. Événement durable `media.modified` (chemin, empreintes avant/après) pour que les blocs Plex/Sonarr/Radarr/Jellyfin puissent s'y abonner (cf. `media.published`).
- Désactivé par défaut au niveau du cluster tant que les tests sur stockage réseau n'ont pas été faits (réglage explicite pour activer le bloc).

## Alternatives écartées
- Politique de bibliothèque « modifier sur place » : écartée par Rémi (2026-10-22).
- Case à cocher sur tous les blocs `mkvpropedit` dès maintenant : change le comportement de flows existants et multiplie les cas à valider ; reportée.
- Sauvegarde de l'en-tête seul : non fiable tant qu'on ne sait pas quels éléments (pistes, étiquettes en fin de fichier) `mkvpropedit` déplace ; la sauvegarde complète est le défaut, la sauvegarde partielle seulement si validée.
- Modification sans sauvegarde : une coupure en cours d'écriture peut rendre le fichier illisible.

## Conséquences et risques
- Modifie les invariants 1 (« source préservée ») et 4 pour ce seul bloc : la sauvegarde remplace la préservation, et coûte l'espace d'un fichier complet par opération sur la même bibliothèque.
- Compatibilité de cluster : un worker sans la version du plugin `mkv` portant le bloc ne reçoit pas le flow (règle existante) ; aucune migration de base prévue en dehors de l'enregistrement de l'intention (à préciser au développement).
- **Non vérifié** : atomicité et verrous de fichiers sur NFS/SMB (la protection repose sur le bail, le verrou disque — valable dans une seule base locale — et l'empreinte, pas sur `flock`) ; comportement de `mkvpropedit` lors d'une coupure ; gros fichiers sur NAS ; interaction avec Plex lisant le fichier.
- Tests à écrire : restauration après échec simulé, reprise après arrêt entre les étapes 3 et 6, refus sans accès direct ou sans bail, refus si lecture Plex, deux exécutions concurrentes sur le même fichier, fichier modifié entre empreinte et écriture.
