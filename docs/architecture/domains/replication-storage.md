---
domain: replication-storage
updated: 2026-10-21
legacy_adr: [0001, 0004, 0014, 0029, 0043, 0049, 0060-peer, 0061-peer]
code:
  - backend/src/Encoding.Api/Infrastructure/Data/
  - backend/src/Encoding.Api/Infrastructure/ClusterReconciliation*.cs
  - backend/src/Encoding.Api/Infrastructure/ClusterEntityReconciliation.cs
  - backend/src/Encoding.Api/Infrastructure/PeerCluster/
  - backend/src/Encoding.Api/Infrastructure/DatabaseMigrator.cs
  - backend/migrations/sqlite/
tags: [sqlite, migrations, maillage, réconciliation, lww, raft, sauvegarde, tombstone, clé de données]
---

# Stockage et réplication

## État courant

### Stockage : SQLite local par serveur
- SQLite est le **seul** moteur (PostgreSQL a été retiré ; ne pas le réintroduire sans décision). Chaque serveur ouvre son `cluster.db` (`ConnectionStrings:Cluster`, défaut `Data Source=./data/cluster.db`) : WAL, clés étrangères, délai d'écriture, transactions d'écriture immédiate. **Jamais** de fichier partagé entre hôtes ni sur stockage réseau.
- Constaté en production le 2026-10-21 (lecture seule, deux hôtes TrueNAS) : un seul conteneur `encoding` par hôte, `ConnectionStrings__Cluster=Data Source=/var/lib/encoding-node/cluster.db` monté depuis un dataset local, WAL actif, aucun conteneur ni connexion PostgreSQL pour encoding, aucune erreur `database is locked` dans les derniers logs. La réconciliation passe par HTTP entre les deux nœuds. Migrer vers PostgreSQL n'est pas justifié pour un cluster à deux nœuds (serveur de base supplémentaire à rendre redondant, point de panne commun). Contenu de `cluster.db` non inspecté (`journal_mode` et cohérence entre copies non vérifiés).
- Les dépôts écrivent encore du SQL au dialecte historique (`FOR UPDATE SKIP LOCKED`, `'infinity'`, `clock_timestamp()`, `gen_random_uuid()`, `<@`) : `SqliteTranslator` l'adapte à l'exécution et échoue sur une construction inconnue. Dates en texte UTC de largeur fixe, UUID et JSON en texte. Ajouter une construction SQL nouvelle = étendre le traducteur et ses tests.
- Migrations : `backend/migrations/sqlite/NNNN_description.sql`, **numéro unique** (le chargement échoue sinon), empreinte SHA-256 dans `schema_migrations.checksum` (modifier une migration appliquée bloque le démarrage : en écrire une nouvelle). `MigrationCatalog.RenamedFrom` relie un fichier renommé. Baseline `0033_baseline.sql`, dernière migration connue `0042_system_logs.sql` (vérifier le dossier avant d'en créer : les threads parallèles doivent se coordonner sur le prochain numéro). Une version enregistrée absente du binaire rend le nœud `degraded`.
- `backend/migrations/0036_library_cluster_tombstones.sql` (racine, hors `sqlite/`) n'est pas chargé : reliquat à supprimer ou confirmer.

### Réplication par défaut : maillage de réconciliation (écriture opportuniste, convergence éventuelle)
- Chaque serveur garde sa base ; les serveurs d'un cluster se découvrent par mDNS (`_encoding._tcp`) et par adresses mémorisées (`cluster_sync_peer_endpoints`, gossip transitif, `Cluster:Reconciliation:Peers` en secours) et échangent un **journal append-only** par `POST /api/v1/cluster/reconciliation/exchange` (lots bornés, `(origine, séquence)` déduplique, relais avec origine conservée).
- Conflits : **dernier écrivain gagne par entité** (horodatage UTC, puis origine et séquence), suppressions = tombstones. Les horloges doivent être synchronisées. Une projection en conflit de contrainte unique est gardée en attente.
- Sécurité : identité RSA par serveur, admissions signées répliquées (tout serveur admis peut approuver une jonction), échanges signés, secrets du journal chiffrés AES-GCM avec la clé de données du cluster (créée à la création du cluster, stockée chiffrée sur chaque serveur). Clés privées et Data Protection dans `PluginSettings:KeyDirectory` : à sauvegarder.
- **Réconcilié** : cluster et nœuds (rôles, capacités, présence, groupes), utilisateurs, demandes de jumelage, jetons API, bibliothèques (chemins, accès, limites, politiques de remplacement, inventaire), flows/brouillons/versions, scripts, profils, **métadonnées des ressources de flow** (`flow_resources`, `flow_resource_versions`, migration 0047), modèles, packs, config et dérogations de plugins (secrets rechiffrés localement), historique durable des exécutions (événements, étapes, checkpoints, reçus), réglages IA.
- **Local, jamais répliqué** : baux et réservations, `execution_work`, attentes actives, scans en cours, files de déclenchement, sessions utilisateur, opérations de remplacement en cours, verrous de disque, `system_logs`, paquets du Store de plugins, fichiers binaires (**octets des ressources de flow** : magasin adressé par empreinte `Resources:Directory`, par serveur ; médias, artefacts, sauvegardes JSON régénérées par chaque serveur).
- Conséquence centrale : **aucune exclusion de travail entre bases indépendantes n'est revendiquée**. Une exécution reste attribuée au serveur qui l'a créée ; la reprise après perte est une relance explicite ([runtime.md](runtime.md)).

### Mode pair à pair optionnel (Raft) : prévisualisation
- `PeerCluster__Enabled=true` (défaut `false`) active un mode distinct : trois serveurs votants, journal Raft (DotNext), transactions SQL répliquées par capture de lignes (`PeerDataSource`), baux avec terme Raft, certificats approuvés, port pair séparé. Même version requise sur tous les pairs (pas de N/N-1), image d'import limitée à 64 Mio. Procédures : `docs/operations/peer-cluster-preview.md`, `peer-cluster-production-todo.md`, règles d'alerte `peer-cluster-alerts.yml`.
- **Contradiction à trancher** : ce mode et le maillage LWW ont des garanties opposées (quorum et exclusion forte vs convergence éventuelle sans quorum). Le principe affiché du produit est « sans quorum ». Voir [open-questions.md](../open-questions.md). Ne pas étendre le mode Raft avant décision.

### Sauvegardes de flows
- Chaque version publiée et chaque révision de brouillon écrit un travail local dans la même transaction ; `{id}.v{version}.json` est écrit atomiquement (temporaire, flush, déplacement sans écrasement) dans `FlowBackups:Directory` (volume séparé). Un fichier existant d'empreinte différente est conservé et signalé `failed`. Retentative 30 s, revérification 10 min, aucune suppression automatique. Statuts `pending/ready/failed` **locaux** à chaque serveur. Les fichiers sont des instantanés récupérables, pas une autorité d'écriture.
- Restauration : réimport par `PUT` dans l'ordre des versions (identité conservée, `expectedVersion` = précédente) ou import JSON/ZIP ([flows.md](flows.md)).

## Invariants
1. Un fichier SQLite appartient à un seul serveur.
2. Toute nouvelle table métier : décider explicitement « réconciliée » (l'inscrire dans `ClusterEntityReconciliation.Tables`, avec tombstone si suppression) ou « locale » (le documenter ici).
3. Une migration appliquée ne se modifie jamais ; numéro unique ; additive tant que N/N-1 n'est pas garanti.
4. Les secrets ne vont jamais en clair dans le journal, les sauvegardes, les exports ni les journaux.

## Ressources de flow (ADR 0006, PR 1)
- Métadonnées (ressource, versions immuables, noms de fichiers, tailles, SHA-256) réconciliées comme les scripts ; **octets** dans `Resources:Directory` (défaut `<Node:DataDirectory>/resources/ab/<sha256>`), jamais dans le journal : c'est l'exception assumée à l'invariant « binaires jamais réconciliés ».
- Envoi : `POST /api/v1/resources/blobs?kind=font&name=…` (corps brut, un fichier, 20 Mio au plus, extension et signature contrôlées), puis `POST /api/v1/resources` ou `…/{id}/versions` qui référencent les empreintes ; 100 Mio par version. Écriture réservée à `admin`, lecture à tout rôle.
- Suppression : refusée (409) tant qu'un flow, toute version confondue, référence l'identifiant ; le message nomme le flow et le bloc. Un fichier n'est effacé du magasin que si aucune version ne l'utilise encore.
- Récupération par un pair (PR 2) : `GET /api/v1/cluster/resources/blobs/{sha256}`, hors session, authentifié par les identités de pairs admis (mêmes en-têtes et fenêtre d'horloge de 2 min que la réconciliation, limiteur `cluster-mesh`). La signature couvre `GET <chemin>` + pair + horodatage, pas de corps ; la requête rejouée dans la fenêtre ne donne que le même fichier. Réponse en flux avec `Range`. `ResourceBlobFetcher` interroge les pairs connus dans l'ordre, reprend un `.part` à l'octet atteint, vérifie taille puis SHA-256 avant de publier ; un fichier faux est supprimé, un fichier incomplet conservé. Deux demandes de la même empreinte partagent un transfert (délai de 2 min). `ResourcePrefetchService` précharge toutes les 30 s la dernière version de chaque ressource absente du magasin.
- Non livré : bloc de polices et interface (PR 3, 4). Les anciennes versions ne sont récupérées qu'à la demande, pas préchargées. Un fichier envoyé sans être référencé reste dans le magasin (nettoyage à prévoir).

## Non livré / limites
- Pas de plage de schéma ni de règle de mise à jour progressive entre serveurs.
- Pas de restauration guidée des sauvegardes de flows ; pas de rétention des sauvegardes.
- Les artefacts doivent être sur un stockage accessible aux serveurs concernés (`Runtime:ArtifactDirectory`) ; un volume local n'établit pas de haute disponibilité inter-hôtes.
- Maillage : un changement qui conserve taille et date n'est pas concerné ici (voir sondage de surveillance dans [runtime.md](runtime.md)) ; deux créations isolées de même nom attendent la résolution de la collision.
