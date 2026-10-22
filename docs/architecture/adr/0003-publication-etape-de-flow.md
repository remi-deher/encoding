---
id: 0003
title: Publication comme étape de flow, distincte de la fin du flow
status: proposed
domain: flows
date: 2026-10-22
decided_by: Rémi (2026-10-22) pour le principe « la fin du flow ne marque pas le transfert » ; détails à valider
supersedes: []
superseded_by: []
tags: [publication, flow, fin-de-flow, plex, radarr, sonarr, jellyfin, media.published, reprise]
---

# 0003 — Publication comme étape de flow, distincte de la fin du flow

## Contexte
Aujourd'hui, la fin du flow déclenche la publication : une fois la sortie validée, le nœud d'accès publie, puis l'exécution passe à `succeeded` et `media.published` est écrit dans la même transaction (`RuntimeTransferRepository`). Rien ne peut donc s'exécuter **après** la publication : Plex, Radarr, Sonarr et Jellyfin ne sont pas actualisés (question ouverte n° 8), et un bloc placé dans le flow s'exécuterait avant que le fichier soit à sa place finale.

FileFlows sépare ces notions (flow « Convertir en HEVC » fourni par Rémi) : le plan (Builder Start, Video Encode), le traitement (Executor, avec sortie d'erreur vers un repli CPU), puis un **nœud explicite « Remplacer l'original »** après lequel le flow continue sur le fichier publié. Avant comme après le traitement, n'importe quelle opération peut s'enchaîner.

## Décision
- Trois notions distinctes : **terminaison du flow** (`base-complete-flow` : résultat du chemin), **traitement** (`base-prepare-processing` puis `base-execute-processing` : plan, sortie temporaire validée, sans toucher la source) et **publication** (étape explicite).
- Nouveau bloc `base-publish-result` (plugin `base`, version mineure suivante), placé après `base-execute-processing`. Il demande la publication au nœud d'accès, sans écrasement (comportement actuel), et rend la main au flow. Sorties : `published`, `deferred` (garde de publication, ex. lecture Plex, voir [runtime](../domains/runtime.md)), `error`. Après `published`, le **fichier courant du flow devient le fichier publié** (chemin et SHA-256) : les blocs suivants (Plex, Radarr, Sonarr, Jellyfin, scripts) travaillent dessus.
- `base-complete-flow` ne publie plus dans un flow qui contient `base-publish-result`. Un flow **sans** ce bloc garde la publication implicite en fin de flow (compatibilité, signalée comme héritée dans l'éditeur).
- Le **remplacement de la source** reste la politique de bibliothèque existante, inchangée et non étendue : le bloc ne remplace rien à lui seul.
- Garanties : publication toujours faite par le nœud d'accès sous bail et époque ; idempotente (même SHA-256 = même résultat) ; `media.published` écrit dans la même transaction que le checkpoint de l'étape. Après perte de nœud ou de bail pendant les blocs qui suivent, la reprise repart **après** le bloc de publication, sans republier.
- **Classification des effets** : chaque composant déclare dans son manifeste `effect` : `none`, `idempotent` ou `exclusive`. `none`/`idempotent` (sondes, vérifications, actualisations Plex/Radarr/Sonarr/Jellyfin, publication identifiée par SHA-256) peuvent être rejoués automatiquement par tout worker éligible, en meilleur effort. `exclusive` (transcodage, remux, modification sur place) exige bail et époque et n'est jamais rejoué silencieusement : reprise explicite comme aujourd'hui. Un composant sans déclaration est `exclusive`.
- **Reprise automatique après publication** : si le worker est perdu après `base-publish-result`, un autre worker éligible reprend la suite depuis le chemin et le SHA-256 enregistrés, avec un nombre de tentatives borné ; la reprise manuelle ne sert qu'une fois ce nombre épuisé. Un worker initiateur perdu ne bloque jamais un worker qui a reçu le relais : il détient son propre bail sur la même exécution.
- **Choix du worker** (reprise et répartition) : le candidat doit satisfaire toutes les contraintes, dans cet ordre : (1) version de plugin requise et capacités d'encodage demandées (`nodes-require-capability`, groupes de nœuds, critères et étiquettes, `nodes-run-on-node`) ; (2) accès au fichier selon la bibliothèque : direct, par transfert ou indisponible (`nodes-require-direct-access`) ; un nœud sans accès direct reçoit le fichier par transfert (SHA-256), jamais en repli silencieux ; (3) disponibilité : en ligne, pas en vidange, dans sa plage horaire. Parmi les candidats, la répartition privilégie la charge la plus faible (tâches actives / `maxConcurrentTasks` du nœud et de la bibliothèque), et à égalité le nœud d'accès direct (pas de transfert). Aucun candidat = attente, jamais de repli sur un nœud non conforme. Les blocs `none`/`idempotent` sans accès au fichier (appels HTTP) ne demandent que la condition (1) et l'accès réseau au service.
- Un échec en aval (actualisation Plex impossible, etc.) **ne défait jamais** la publication. Les blocs d'actualisation exposent une sortie « avertissement » ; l'état final de l'exécution dans ce cas est à décider (voir risques).
- `media.published` reste un événement d'audit durable, et non un déclencheur. Aucun consommateur en arrière-plan.

## Alternatives écartées
- Service en arrière-plan abonné à `media.published` : plus complexe à exploiter et à rendre fiable en cluster ; écarté par Rémi (2026-10-22).
- Statu quo (publication implicite à la fin) : rien ne peut suivre la publication.
- Chaque bloc d'actualisation publie lui-même : couple publication et intégrations, risque de double publication.

## Conséquences et risques
- Compatibilité de cluster : le bloc exige une version du plugin `base` ; un worker plus ancien ne reçoit pas le flow (règle existante). Une étape de publication persistée dans le checkpoint peut demander une migration additive.
- Invariants 1 et 4 inchangés : la source reste préservée et aucun remplacement n'a lieu depuis un flow seul. L'invariant 2 devient : « jamais de reprise silencieuse d'un effet incertain, sauf pour les composants déclarés idempotents ».
- Pas de relais (`nodes-handoff`) après la publication : la suite est courte et se rejoue depuis l'état enregistré. Un état intermédiaire `published` est ajouté ; `succeeded` n'est écrit qu'à la fin du flow (comportement actuel conservé pour les flows sans `base-publish-result`).
- À décider : état final d'une exécution publiée dont une actualisation aval a échoué (réussi avec avertissements, ou nouvel état) ; nombre de tentatives des actualisations.
- Lien avec [0002](0002-modification-mkv-sur-place.md) : `media.modified` suit la même logique.
- Tests à écrire : reprise après perte du worker initiateur par un worker conforme aux capacités et à l'accès ; aucun candidat conforme = attente ; répartition selon la charge ; composant sans `effect` traité comme `exclusive` ; crash entre publication et bloc suivant (pas de double publication), garde Plex (`deferred`), échec d'actualisation sans annulation de la publication, flow ancien sans le bloc, nœud sans la version requise.
- Non vérifié en réel : NAS/NFS (atomicité du renommage), Plex/Radarr/Sonarr réels, reprise multi-hôtes.
- À la livraison : mettre à jour [runtime](../domains/runtime.md), [fileflows-v3-flow](../../operations/fileflows-v3-flow.md) et fermer la question ouverte n° 8.
