---
domain: runtime
updated: 2026-10-21
legacy_adr: [0005, 0009, 0015, 0016, 0018, 0020, 0021, 0028, 0030, 0033, 0054, 0058, 0059, 0064, 0066, 0075]
code:
  - backend/src/Encoding.Api/Executions/
  - backend/src/Encoding.Api/Libraries/
  - backend/src/Encoding.NodeAgent/Runtime/
  - backend/src/Encoding.Contracts/Executions/
  - backend/src/Encoding.Core/Runtime/
tags: [exécution, bail, époque, checkpoint, relais, attente, verrou, découverte, surveillance, publication, remplacement, landlock, intégrité]
---

# Runtime : exécution, intégrité et publication

Un worker exécute une **version publiée précise** d'un flow, de bout en bout, sur un seul nœud par défaut. Le modèle de flow est dans [flows.md](flows.md), les plugins dans [plugins.md](plugins.md).

## Registre des exécutions
- États : `queued`, `running`, `cancelling`, `succeeded`, `failed`, `cancelled`. Une transition et son événement sont écrits dans la même transaction ; rapports idempotents (identifiant d'événement + révision attendue) ; transitions terminales figées. Annulation en cours = demande confirmée après arrêt des processus. Une relance conserve l'échec et crée une tentative liée (une seule relance concurrente).
- L'historique durable est réconcilié entre serveurs ; les tâches et baux actifs restent locaux au serveur qui les a créés ([replication-storage.md](replication-storage.md)). `GET /api/v1/executions` (≤ 200, actives d'abord), `…/{id}/events`, `…/{id}/pipeline` (étapes, commandes, sorties tronquées à 32 Ko, secrets masqués ; stdout/stderr des scripts et plugins exclus).
- Création uniquement depuis une bibliothèque (version de flow attribuée), jamais avec une définition arbitraire.

## Attribution, baux, reprise
- Chaque serveur attribue **uniquement** les travaux de sa base locale, en transaction d'écriture immédiate. Le worker doit être présent, accepter du travail ([cluster.md](cluster.md)), avoir une place, les outils et encodeurs vérifiés, les versions exactes des plugins et un accès direct ou par transfert. Segments : `nodes-run-on-node`, `nodes-require-capability`, `nodes-require-direct-access` (strict ou préférence) contraignent le worker d'un segment.
- **Reprise après perte du worker** (ADR [0004](../adr/0004-reprise-au-checkpoint.md)) : à l'expiration du bail, l'exécution est rendue à la file des relais (même identité, époque suivante, réservation conservée, verrous de disque libérés) et réclamée par un worker éligible selon les règles habituelles (capacités, accès au média, limites, charge), au plus 2 fois (`resume_count`). Étapes à effet incertain (`base-script`, `mkv-edit-in-place`, recherches Sonarr/Radarr, plugins inconnus), sonde d'attente en cours ou règle de flow `nodes-retry-on-worker-loss` : échec comme avant. Sans worker éligible pendant 2 minutes : échec.
- Bail 60 s renouvelé toutes les 15 s, **époque** croissante, réservation unique par bibliothèque/chemin. Tout rapport, renouvellement, transfert et publication vérifie époque et expiration ; un ancien worker est refusé (409). Checkpoint et événement dans la même transaction.
- **Perte du worker** : la tentative échoue, la réservation est libérée, **aucune réattribution silencieuse sous le même identifiant**. Reprise = nouvelle exécution depuis l'entrée, manuelle ou par `nodes-retry-on-worker-loss` (1 à 10 tentatives, cible worker/groupe/étiquettes), refusée automatiquement si une étape du précédent essai est non idempotente. Un script ou plugin interrompu avant son checkpoint a un effet incertain : échec + relance explicite.
- Plafonds de traitement : global nœud, global bibliothèque, couple nœud/bibliothèque ; tous doivent autoriser ; maxima sans réservation ; diminuer une limite ne tue pas l'actif.
- Annulation : contrôlée avant chaque étape et pendant les processus externes ; confirmée seulement après arrêt de l'arbre de processus et nettoyage ; perte du bail interdit toute publication.

## Relais, attentes, verrous
- `nodes-require-capability` accepte un **type d'encodage** (`encoderKind`, plugin `nodes` 1.5.0) avec « Encodeur ou codec requis » : « Matériel » n'accepte que les encodeurs matériels (QSV, NVENC, VAAPI) utilisables sur le worker et exclut le CPU (`libx265` ne satisfait pas `hevc` en mode matériel) ; « CPU » n'accepte que les encodeurs logiciels ; « Peu importe » garde le comportement antérieur. Sans codec renseigné, tout encodeur du type choisi convient.
- Le type d'encodage « Selon le profil d'encodage du flow » (`encoderKind=profile`, plugin `nodes` 1.6.0) exige un worker qui a un encodeur **utilisable** parmi les cibles du profil de chaque bloc vidéo du flow (cible CPU de repli comprise si « Réessayer en CPU » est actif). Le codec et l'encodeur du bloc sont ignorés ; sans bloc vidéo avec profil dans le flow, aucun worker n'est éligible.
- « Bibliothèque sur ce nœud » accepte « Et pouvoir encoder avec le profil du flow » (`requireProfileEncoder`, plugin `nodes` 1.7.0) : le nœud répond « Oui » seulement s'il a la bibliothèque en accès direct **et** un encodeur utilisable du profil ; sinon « Non ». L'éligibilité n'exige alors pas la branche « Oui » d'un worker sans encodeur, qui peut donc prendre le flow puis confier le fichier à un nœud capable (relais « Tout worker éligible » suivi de « Exiger une capacité » selon le profil).
- `nodes-library-access` (plugin `nodes` 1.4.0) : condition « la bibliothèque est montée sur ce nœud » (sorties `yes` / `no`). `yes` si le worker est le nœud d'accès du fichier et en accès direct, `no` s'il a reçu une copie par transfert. Enchaînement type pour un encodage : `yes` → on encode sur place ; `no` → `nodes-handoff` (autre worker éligible) puis `nodes-require-capability` (encodeur voulu), le nœud d'accès restant celui qui publie. Pour un remux ou un changement d'en-tête, `nodes-require-direct-access` en début de flow. Le nœud d'accès reste celui de la première attribution, même après un relais.
- `nodes-handoff` : cède la suite à un autre worker à un checkpoint durable, sous nouveau bail, même exécution, **même réservation** ; cible absente/occupée/incompatible = attente (jamais repli sur le précédent) ; fichiers identifiés par SHA-256 ; le nœud d'accès de la bibliothèque reste fixé à la première attribution. Relais vers soi-même = pas de transfert. Un worker n'ayant pas la version de plugin requise ne reçoit pas le flow.
- `base-wait` : attente durable (`timer`, `nodeState`, `workload`, `block` = condition d'un plugin) sans occuper de worker ni de plafond ; le média reste réservé ; reprise à l'étape suivante avec le contexte inchangé ; `timeout` non relié = échec explicite ; `ExecutionWaitService` (chaque serveur, 5 s) décide par mise à jour conditionnelle (une seule décision). Politique média pendant l'attente : `keep` / `beside` / `overwrite` (ce dernier passe toujours par la garde de la bibliothèque).
- `base-disk-lock` / `base-disk-unlock` : verrou nommé par disque (table locale `disk_locks`, prise `INSERT … ON CONFLICT … WHERE`, **fenced** par bail+époque, suit le bail, libéré à la fin, à la récupération, à l'attente, au relais). `busy` n'attend jamais ; le flow le relie à `base-defer-processing` ou `base-wait`. Exclusion garantie **dans une seule base locale** seulement.

## Découverte et surveillance des bibliothèques
- Demande de découverte durable et idempotente, prise par un nœud en **accès direct en lecture** (ou un serveur sans capacité de traitement), bail 60 s/15 s vérifié à la remise ; ignore les liens symboliques et dossiers `.encoding*` ; filtre les extensions selon le flow ; limite 10 000 fichiers (dépassement = échec, jamais d'inventaire tronqué) ; un succès remplace l'inventaire de façon atomique.
- Surveillance : sondage périodique (60 s par défaut, 15 à 86 400 s), fichier stable ≥ 30 s, réception unique par fichier/version/taille/date ; un échec ou une annulation **ne boucle pas** (relance explicite). Pas d'événement système de fichiers : un changement qui conserve taille et date n'est pas vu ; copier sous un nom temporaire puis renommer.

## Outils, bac à sable, scripts
- Outils : FFmpeg/ffprobe, MKVToolNix, MediaInfo, ImageMagick, Python, Node dans l'image. Adaptateurs sans shell, sorties bornées, arbres de processus arrêtés. MKVToolNix lancé avec `LC_ALL=C.UTF-8`. `mkvmerge/mkvextract/mkvpropedit` : code 1 = avertissement = succès.
- **Landlock obligatoire** pour scripts, plugins, CLI IA : écriture limitée à l'espace temporaire de l'exécution, pas d'accès au jeton ni aux montages média. Landlock indisponible = capacités non annoncées, processus non exécutés, **aucun repli non confiné**. Pas de conteneur par plugin. L'enfant isolé reçoit un environnement minimal (liste blanche). Voir la décision de sécurité ouverte sur `/proc` dans [open-questions.md](../open-questions.md).
- `base-script` avec `useTools: true` reçoit `tools.run/path/allowed` (ffmpeg, ffprobe, mkvmerge, mkvextract, mkvpropedit, mediainfo, magick ; la liste blanche n'est **pas** une frontière de sécurité, Landlock l'est) ; le worker annonce `script-tools` ; durée max 86 400 s ; pas de GPU dans le bac à sable (`/dev/dri` non autorisé).
- Éligibilité encodeur : un encodeur compte seulement s'il a passé la mire synthétique ; `retryWithCpu` (désactivé par défaut) relance en CPU après échec matériel.

## Transfert, publication, remplacement
- Transfert par WebDAV inter-nœuds : SHA-256, reprise par Range, seuls des blobs complets sont publiés. `Runtime:ArtifactDirectory` doit être durable et accessible aux serveurs concernés. Rétention des artefacts désactivée par défaut, `DryRun` disponible ([auth-ops.md](auth-ops.md)).
- Sortie validée (fichier non vide, pistes attendues, durée cohérente, décodage complet ; `probe-validate-output` configurable) puis publiée **sans écrasement** dans `.encoding-results/<executionId>/result.<ext>` par le nœud d'accès, après re-vérification source, résultat, bail. La source est préservée. Événement durable `media.published` (chemin + SHA-256), qui sert à l'audit. **Publication par bloc** (ADR [0003](../adr/0003-publication-etape-de-flow.md)) : `base-publish-result` publie au milieu du flow, l'exécution reste active (bail et réservation conservés) et le flow continue sur le fichier publié (Plex, Radarr, Sonarr, scripts) ; sans ce bloc, la publication reste à la fin du flow.
- **Remplacement de la source** : politique de bibliothèque, **désactivée par défaut** (période de grâce 1 à 8 760 h, 168 par défaut). Seul le nœud d'accès l'effectue : vérifier empreinte source → copie de sauvegarde `.encoding-replaced/<exec>/original.<ext>` → copie du résultat voisine de la cible + re-vérification → bascule atomique (renommage, ou publication sans écrasement + suppression de l'ancien nom) → vérification d'empreinte, restauration si échec. Reprenable après perte. Restauration pendant la grâce (`POST …/replacement/rollback`, réserve le média, refus si modifié depuis). Purge après échéance seulement si le remplaçant existe encore. États dans `source_replacements`. Un échec de remplacement n'échoue pas l'exécution. Marques anti-boucle `media_replacement_marks`.
- **Gardes de publication** : un manifeste de plugin peut déclarer `publicationGuard` (Plex : `plex-check-playback`) ; avant tout remplacement le nœud d'accès invoque la garde ; seule une autorisation explicite (`idle`) passe, attente plafonnée (30 min par défaut, 720 max), tout doute = refus, résultat distinct conservé. La garde s'applique à tous les remplacements du cluster tant que le plugin est configuré.

## Invariants
1. Une seule tâche à la fois modifie un média ; la source est préservée jusqu'à validation et publication réussies.
2. Époque + bail partout ; jamais de reprise d'un effet incertain. Une étape rejouable interrompue est rejouée par un autre worker depuis le dernier checkpoint (ADR [0004](../adr/0004-reprise-au-checkpoint.md)).
3. Les opérations sont idempotentes ou détectent le doublon ; pas de boucle de relance automatique.
4. Le remplacement n'a lieu que par la politique explicite de la bibliothèque, jamais depuis un flow seul. Seule exception : `mkv-edit-in-place` modifie les en-têtes de la source (ADR [0002](../adr/0002-modification-mkv-sur-place.md)) avec copie intégrale vérifiée et restauration.

## Non livré / limites
- **Modification sur place** : bloc `mkv-edit-in-place` (ADR 0002) sur le worker à accès direct ; copie intégrale, contrôle `mkvmerge -J`, restauration. Pas d'événement `media.modified`, pas de reprise automatique depuis l'intention, non vérifié sur NFS/SMB.
- Pas de réattribution automatique entre serveurs ; pas de relance déclenchée par `media.published` (les actualisations après publication se placent dans le flow, après `base-publish-result`) ; pas d'interface de suppression/recréation des verrous de disque par accès bibliothèque (nom saisi dans le bloc).
- Non vérifié en réel : Landlock dans le conteneur de production, GPU, Plex/Jellyfin réels, NAS/NFS (atomicité du renommage), reprise multi-hôtes sous charge.
