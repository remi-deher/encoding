---
id: 0024
title: Planificateur de placement des flows de bout en bout et minimisation des transferts
status: implemented
domain: runtime
date: 2026-10-27
decided_by: Rémi Deher
supersedes: []
superseded_by: []
tags: [placement, planificateur, claim, handoff, transferts, webdav, gpu, acces-direct]
---

# 0024 — Planificateur de placement des flows de bout en bout et minimisation des transferts

## Contexte

Aujourd'hui, l'attribution d'une exécution de flow est décidée segment par segment (`RuntimeHandoffPolicy.Segment`, `RuntimeEligibility.CanRun` dans `RuntimeRepository.ClaimAvailableAsync`). Dès qu'un worker est éligible pour le premier segment allant de l'entrée jusqu'au prochain bloc `nodes-handoff`, il peut réclamer la tâche (`ClaimAsync`).

Dans les flows réels combinant accès direct (`nodes-require-direct-access`, `nodes-library-access`), encodage matériel (`video-transcode`, `nodes-require-capability`) et post-traitements (assemblage des sous-titres, normalisation audio, contrôle d'intégrité, publication), ce fonctionnement purement local et glouton entraîne un phénomène de va-et-vient (« ping-pong ») :
- Le nœud d'accès direct A (ex. CPU) commence le traitement (remux initial) ;
- Un `nodes-handoff` envoie le fichier par WebDAV vers le nœud B (ex. GPU) pour encodage (transfert de 467 Mo à 1,5 Go) ;
- Un second `nodes-handoff` renvoie le fichier vers A pour des opérations audio/sous-titres ou polices locales (nouveau transfert de 467 Mo à 1,5 Go) ;
- Un troisième relais peut renvoyer vers B pour contrôle puis retour vers A pour publication.

Chaque saut transfère l'intégralité du fichier multimédia sur le réseau local, sature les disques et la bande passante, et multiplie les risques de timeout de bail. De plus, pour éviter ces écueils, les auteurs de flows doivent actuellement concevoir manuellement des graphes complexes truffés de blocs de relais conditionnels.

Rémi souhaite que le moteur analyse toutes les capacités requises par le flow dès que la source est connue, et choisisse le bon placement dès le départ afin d'éviter les transferts superflus.

---

## Décision

Ajouter au moteur d'exécution une **couche de planification de placement de bout en bout** (*Whole-Flow Placement Planner*), sans supprimer les blocs de flow existants :

### 1. Analyse des besoins par chemin de flow (fonction pure)
Dès que la source est inspectée (métadonnées d'origine et flux connus, soit lors du pré-scan sur le nœud d'accès direct, soit au démarrage de l'exécution), le planificateur évalue la topologie du flow et extrait pour chaque chemin acyclique possible l'ensemble structuré de ses exigences (`FlowPathRequirements`) :
- **Encodeurs vidéo et audio requis** : codecs cibles, profil d'encodage, exigence matérielle (`hardware`), CPU pur ou repli CPU autorisé (`retryWithCpu`).
- **Accès à la bibliothèque** : lecture directe (`mode = 'direct'`), écriture directe (`can_write`) exigée par les étapes de modification sur place (`mkv-edit-in-place`) ou de publication (`base-publish-result`).
- **Outils système et versions minimales** : `ffmpeg`, `ffprobe`, `mkvtoolnix`, `python`, `script-tools`.
- **Plugins et versions d'exécution** : manifestes de plugins requis et compatibilité des versions annoncées.
- **Ressources et inventaires** : jeux de polices déclarés (`FontSets`), ressources partagées du catalogue.
- **Contraintes explicites de flow** : respect strict des directives posées par l'auteur (`nodes-run-on-node`, `nodes-require-capability`, `nodes-handoff`, `nodes-library-access`).

### 2. Règle de sélection du nœud : priorité au nœud d'accès direct
Le planificateur choisit le placement selon trois niveaux de priorité décroissants :
1. **Priorité 1 — Nœud d'accès direct (Zéro transfert réseau)** :
   Si le nœud d'accès direct à la bibliothèque (`mode = 'direct'`) est en ligne et dispose des capacités requises pour exécuter tous les chemins probables du flow (par exemple : flux vidéo simplement copié sans transcode, ou transcodage CPU supporté localement), **ce nœud est choisi pour l'intégralité du flow**. Aucun transfert WebDAV n'a lieu.
2. **Priorité 2 — Plan regroupé en 3 phases (Au plus 1 transfert aller / 1 transfert retour)** :
   Si le nœud d'accès direct ne dispose pas d'une capacité indispensable (par exemple : transcodage matériel HEVC QSV ou NVENC disponible uniquement sur un nœud distant .51) :
   - Le planificateur scinde le traitement en au plus 3 phases logiques regroupées :
     - *Phase A (Préparation locale)* : Inspection rapide, extraction de métadonnées et validation sur le nœud d'accès direct (zéro copie volumineuse).
     - *Phase B (Exécution lourde distante)* : **Un seul transfert WebDAV d'envoi** vers le nœud spécialisé (.51), qui exécute l'intégralité des opérations d'encodage (vidéo, audio, filtrage).
     - *Phase C (Assemblage et publication locale)* : **Un seul transfert WebDAV de retour** vers le nœud d'accès direct (.50), qui réalise l'assemblage MKV final (polices, chapitres, sous-titres ASS), le contrôle de cohérence, le décodage de contrôle (ADR 0007), la publication atomique locale et les actualisations (Plex, Sonarr, Radarr).
   - Les allers-retours multiples intermédiaires sont strictement proscrits par le plan.
3. **Priorité 3 — Cas où aucun nœud ne satisfait un chemin requis** :
   - **Décision tranchée** : **Échec immédiat et visible à la planification**, nommant explicitement la capacité manquante (`"Capacité manquante : aucun worker en ligne ne dispose de l'outil ou encodeur <X> pour ce flow"`), plutôt qu'un repli dégradé sur le découpage glouton.
   - *Justification* : Un repli glouton masquerait l'impossibilité de mener le flow à son terme, consommerait inutilement des I/O disque et du réseau pour transférer des centaines de mégaoctets, pour finalement échouer au milieu du traitement. L'échec précoce protège les ressources du cluster et alerte immédiatement l'administrateur, conformément à `AGENTS.md` et `BACKEND_RULES.md`.

### 3. Branches conditionnelles et replanification à chaud
- Les conditions fondées sur les métadonnées de la source (ex. `conditions-video-codec` « Déjà HEVC ? », `conditions-video-hdr` « HDR présent ? ») sont **évaluées statiquement** lors de la planification initiale puisque les métadonnées de la source sont déjà scannées. Le planificateur élague immédiatement les branches non concernées.
- Si une condition dépend d'une sortie dynamique (résultat d'un script ou mesure au vol) : le planificateur retient l'union des besoins des branches potentielles. Si à l'exécution la condition emprunte une branche nécessitant un nœud différent du nœud actuellement assigné, un relais planifié intervient au point de bifurcation.

### 4. Stabilité du plan à la reprise (ADR 0004)
- Le plan de placement calculé est associé à l'exécution.
- En cas d'expiration du bail d'un worker (`RuntimeRecoveryService`), le nœud assigné est attendu pendant la grâce de reprise (2 minutes).
- Si le nœud ne revient pas ou si la reprise doit s'effectuer sur un autre nœud : le planificateur est réinvoqué à partir du dernier checkpoint durable (`execution_steps.checkpoint`). Si le nœud d'accès direct peut terminer les étapes restantes (par exemple si l'encodage lourd était déjà validé au checkpoint), la tâche lui est directement réattribuée.

### 5. Propriété des tâches (ADR 0015) et coordination (ADR 0016 / 0023)
- L'autorité d'attribution reste sous la responsabilité du serveur propriétaire (signature `tasks/v1/events/<id>`).
- Le calcul du plan de placement s'exécute côté serveur lors de la préparation des claims.
- Si le meilleur nœud identifié pour l'ensemble du flow est géré par un autre serveur du cluster possédant un accès direct local à la même bibliothèque, le planificateur peut recommander un transfert de tâche inter-serveurs (ADR 0016) évitant tout transfert de fichier par le worker.

### 6. Journalisation et observabilité
- La décision de placement est consignée dans un événement structuré `placement_planned` dans `execution_events`, visible dans le pipeline UI et l'export des journaux :
  `"Placement planifié : nœud .50 retenu pour tout le flow (accès direct, 0 transfert réseau)."` ou
  `"Placement planifié : .50 (préparation) → .51 (encodage HEVC QSV) → .50 (assemblage/publication), 1 transfert aller/retour."`

### 7. Persistance SQLite (Migration locale 0062)
- Ajout d'une colonne additive `placement_plan TEXT` dans la table locale `execution_work`.
- Table strictement **locale** : aucune modification du format de réconciliation du maillage LWW.
- Les reprises et transferts inter-serveurs transportent ce plan dans le snapshot chiffré du checkpoint réconcilié.

---

## Alternatives écartées

1. **Conserver le découpage glouton par segment et imposer aux auteurs de flows de gérer les relais manuellement** :
   Écarté : complexité excessive pour les utilisateurs, risque d'erreurs élevé, impossibilité d'optimiser automatiquement les cas courants comme la copie de vidéo.
2. **Repli silencieux sur le découpage glouton par segment quand aucun nœud ne satisfait le plan complet** :
   Écarté : transférer 1 Go sur le réseau pour échouer 3 blocs plus loin parce qu'un outil manque est un gaspillage inacceptable de ressources disque et réseau.
3. **Coordinateur centralisé permanent ou ordonnanceur maître** :
   Écarté : incompatible avec le principe fondateur du cluster d'hôtes autonomes sans serveur central permanent (`AGENTS.md`).
4. **Partage du stockage de travail via montage réseau unique (NFS/SMB)** :
   Écarté : les outils de traitement travaillent sur des fichiers locaux ; ne jamais partager directement des fichiers de base de données ou de travail intermédiaire sur un système de fichiers distant non fiable (`AGENTS.md`).

---

## Conséquences et risques

- **Invariants préservés** : La source reste préservée jusqu'à validation et publication ; les réservations, baux, époques et barrières d'écriture (ADR 0014) restent strictement respectés.
- **Réduction drastique des I/O réseau** : Pour les fichiers dont la vidéo est copiée, suppression totale de 100 % des transferts réseau. Pour les fichiers transcodés, réduction à un unique aller-retour strictement borné.
- **Rétrocompatibilité cluster** : Entièrement transparente. Un ancien worker exécute sans modification les baux attribués par le serveur doté du planificateur.
- **Migration** : Migration SQLite additive 0062 (`0062_flow_placement_plan.sql`).
- **Tests à écrire** :
  - Tests unitaires purs sur le calcul des besoins d'un flow (`FlowPlacementPlannerTests`) : analyse des chemins, détection des encodeurs et outils, sélection du nœud optimal.
  - Tests d'intégration sur `ClaimAvailableAsync` avec simulation multi-nœuds (nœud d'accès direct capable vs nœud nécessitant un relais regroupé).
  - Tests des cas d'échec précoce (capacité introuvable) et de bifurcation conditionnelle.
  - Tests de reprise après panne avec recalcul de placement.
- **Non vérifié en conditions réelles** : Charge multi-hôtes avec plusieurs transferts concurrents sur le cluster réel (.50/.51).

---

## Annexe : Cartographie du code existant

### 1. Où se décide le claim ?
- **Fichier** : [`backend/src/Encoding.Api/Executions/RuntimeRepository.cs`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.Api/Executions/RuntimeRepository.cs)
- **Méthodes clés** :
  - `ClaimAsync(Guid worker, CancellationToken ct)` (lignes 166-187) : point d'entrée invoqué périodiquement par chaque worker actif.
  - `ClaimAvailableAsync(Guid worker, ClaimCursor? after, CancellationToken ct)` (lignes 193-424) :
    - Récupère les capacités du worker (`cluster_nodes.capabilities`), sa limite de concurrence (`max_concurrent_tasks`), son calendrier (`NodeSchedule`) et son identité (`nodeIdentity`).
    - Parcourt les tâches prêtes par page de 100 (`FOR UPDATE OF e,w SKIP LOCKED`).
    - Filtre par `item.Selector.Accepts(...)` (l. 252).
    - Filtre par `eligibility.CanRun(...)` (l. 253).
    - Filtre par accès bibliothèque (`library_node_accesses`, l. 295-335) : direct ou transfert autorisé.
    - Émet le bail `RuntimeLease` et enregistre l'événement de démarrage `running`.

### 2. Où le segment est-il calculé ?
- **Fichier** : [`backend/src/Encoding.Contracts/Executions/RuntimeHandoffPolicy.cs`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.Contracts/Executions/RuntimeHandoffPolicy.cs)
- **Méthode clé** :
  - `Segment(FlowDefinition flow, string entryNodeId, Func<FlowConnectionDefinition, bool>? follow = null)` (lignes 115-130) :
    - Effectue un parcours en profondeur à partir de `entryNodeId`.
    - Dès qu'un nœud correspond à `IsHandoff(node)` (`nodes-handoff`), le nœud est collecté mais ses liens sortants ne sont pas explorés (`continue;`).
    - Le segment retourné représente donc l'horizon d'exécution immédiat du worker courant.

### 3. Où le `Context.Plan` du checkpoint est-il utilisé ?
- **Dans l'éligibilité** : [`backend/src/Encoding.Api/Executions/RuntimeEligibility.cs`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.Api/Executions/RuntimeEligibility.cs) (lignes 66-71) :
  - Les étapes de traitement différées accumulées dans le builder FFmpeg (`context.Plan`) sont converties en nœuds virtuels (`plan-0`, `plan-1`...) et concaténées au segment pour vérifier que le worker qui reprend possède bien les outils et encodeurs requis pour exécuter le plan préparé.
- **Dans le runner** : [`backend/src/Encoding.NodeAgent/Runtime/RuntimeFlowRunner.cs`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.NodeAgent/Runtime/RuntimeFlowRunner.cs) (lignes 250, 525, 571, 671) :
  - `FfmpegBuilderPolicy` accumule les opérations dans `context.Plan` et ne matérialise l'encodage qu'au moment de `base-ffmpeg-builder-execute` ou `base-execute-processing`.

### 4. Composants et services réutilisables
- **Analyse des encodeurs** : [`Encoding.Api.Flows.MediaCodecPolicy`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.Api/Flows/MediaCodecPolicy.cs) :
  - `ResolveVideoEncoder`, `ProfileEncoderUsable`, `AudioEncoder`.
- **Catalogue des composants** : [`Encoding.Api.Flows.FlowComponentRegistry`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.Api/Flows/FlowComponentRegistry.cs) :
  - `Resolve(node.Component)` fournit `RequiresWriteAccess`, `RequiredTools`, `MinimumRuntime`.
- **Catalogue des encodeurs** : [`Encoding.Contracts.Flows.VideoEncoderCatalog`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.Contracts/Flows/VideoEncoderCatalog.cs).
- **Accès bibliothèques** : table `library_node_accesses` (modes `direct`, `transfer`).
- **Attribution et coordination** : [`TaskOwnershipRepository`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.Api/Executions/TaskOwnershipRepository.cs) et [`ClusterTaskCoordinator`](file:///c:/Users/remi2/Documents/encoding/backend/src/Encoding.Api/Executions/ClusterTaskCoordinator.cs) (ADR 0015, 0016).
