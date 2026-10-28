---
id: 0024
title: Choix du nœud avant exécution, par défaut le nœud à accès direct
status: accepted
domain: runtime
date: 2026-10-28
supersedes: []
superseded_by: []
---

# 0024 — Choix du nœud avant exécution

## Constat

Un flow qui change de nœud en cours de route (remux sur A, encodage sur B, audio et sous-titres sur A, vérification sur B) multiplie les transferts de fichiers complets. Une première tentative de planificateur (PR #443, fermée) ajoutait des écritures au chemin de réclamation (verrou SQLite du 23/10) et excluait des workers valides. Elle est abandonnée, y compris sa migration 0062.

## Décision

**Comportement par défaut.** Sans bloc de passation ni de choix de nœud, le flow s'exécute entièrement sur un nœud worker qui a l'accès direct à la bibliothèque. Aucun transfert, aucune configuration.

**Quand le flow contient des blocs de passation, de choix de nœud, de capacité ou d'accès** (`nodes-handoff`, `nodes-run-on-node`, `nodes-require-capability`, `nodes-require-direct-access`, `nodes-library-access`), le nœud est choisi **avant les opérations**, d'après les contraintes de tout le flow, par ordre de priorité :

1. Un nœud explicitement désigné (`nodes-run-on-node`, passation nommée) est respecté à la lettre. S'il ne peut pas faire le travail (capacité manquante, hors ligne), l'exécution échoue tôt avec une raison explicite, sans repli silencieux.
2. À défaut, le nœud à accès direct, s'il réunit les capacités requises pour l'étape (une capacité manquante l'exclut de cette étape).
3. À défaut, un nœud capable de faire toute la tâche, le fichier lui étant transféré. S'il n'y en a pas, le plan propose le découpage minimal (le moins de transferts) ; s'il n'existe pas, échec clair « aucun nœud ne réunit X et Y ».

Entre candidats de même niveau, départage par charge disponible. Aucun nom de nœud, de bibliothèque ou de flow n'apparaît dans le code : seules comptent les capacités, les accès et les désignations des blocs.

**Scan.** Les nœuds à accès direct scannent (mediainfo, métadonnées) sans être désignés comme ceux qui traitent. Ces faits (codec, HDR, pistes) sont figés dans l'exécution à sa création et alimentent le plan.

**Contraintes de conception.**
- Le plan est calculé en lecture seule, à la création de l'exécution et à chaque reprise ; aucune écriture supplémentaire dans le chemin de réclamation.
- Les blocs restent la source de vérité ; aucun bloc supprimé, aucun flow existant réécrit.
- Mise en file automatique : seul un serveur dont le nœud a l'accès direct crée l'exécution ; si aucun serveur n'a d'accès direct, tout serveur peut le faire (6826a62).

## Mise en œuvre (première tranche)

Réclamation : pour une exécution fraîche (sans checkpoint ni passation) dont le segment ne contient ni `nodes-run-on-node` ni `nodes-library-access`, un worker par transfert ne réclame pas tant qu'un worker à accès direct, en ligne, **capable** de tout le segment (même contrôle `CanRun` que pour une réclamation) et **disposant d'un créneau libre** existe. Un worker direct saturé ne retient pas le travail. Contrôle en lecture seule, sans écriture ajoutée. Le mode « préférer l'accès direct » du bloc existant bénéficie du même contrôle de capacités. **Échec précoce.** Une exécution neuve restée en file plus de 2 minutes est vérifiée par le service de reprise, en lecture seule : si aucun worker déclaré du cluster ne peut la faire (capacités, désignation de nœud, accès à la bibliothèque), elle échoue avec une raison explicite. Seule l'impossibilité statique compte : un nœud hors ligne, en drainage, en quarantaine ou hors plage horaire reste un destinataire possible, et un cluster sans aucun worker déclaré n'échoue rien. Pas encore livré : le découpage minimal multi-nœuds.

## Acceptation

Scénarios synthétiques multi-nœuds : défaut direct ; désignation respectée ; désignation impossible = échec précis ; capacité seulement ailleurs = transfert unique ; aucun nœud adapté = échec précoce ; aucune régression de réclamation ni de verrou SQLite sous charge.

## Limites

Pas de migration prévue a priori (numéro 0062 libre). Pas de validation en production avant le go de Rémi.
