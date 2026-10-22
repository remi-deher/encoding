# Écarts constatés et décisions en attente

Dressé le 2026-10-21 en relisant les anciens ADR contre le code de `dev`. Chaque point dit ce qui est constaté, pourquoi cela compte, et ce qui est proposé. **Rien n'est tranché ici** : ce sont les décisions de Rémi. Une fois décidé, déplacer le point dans la fiche du domaine (et dans un ADR si la décision est structurante).

## À traiter en priorité (production)

### 1. Authentification par défaut : décidé (2026-10-21)
- Décision de Rémi : le setup d'un admin est imposé. `Auth:Enabled` vaut désormais `true` par défaut ; les écritures MCP exigent un jeton à portée explicite. Voir [auth-ops](domains/auth-ops.md). Reste à faire : vérifier sur les deux hôtes TrueNAS (comptes existants ? sinon l'assistant d'admin apparaîtra à la mise à jour) et ajouter un jeton MCP scopé pour les clients existants.

### 2. Deux mécanismes de réplication aux garanties opposées
- Constaté : le **maillage LWW sans quorum** (par défaut, [replication-storage](domains/replication-storage.md)) et le **mode Raft optionnel** `PeerCluster__Enabled` (quorum, transactions répliquées, baux avec terme) coexistent dans le code (`Infrastructure/PeerCluster/`, ~15 fichiers, tests dédiés, 3 documents d'exploitation). Les principes affichés du produit (« sans quorum ») ne couvrent que le premier.
- Impact : un lecteur ou un agent ne sait pas quel modèle étendre ; les garanties d'exclusion de travail diffèrent radicalement.
- Options : (a) garder Raft comme aperçu explicitement expérimental et le geler ; (b) le retirer ; (c) en faire un mode supporté et le documenter comme tel. Recommandation : (a) tant qu'aucun besoin de production ne l'exige, et ne pas l'étendre.

### 3. Sécurité du bac à sable : `/proc` et `/sys` lisibles
- Constaté : Landlock autorise la lecture de `/proc` et `/sys` (ajouté pour les CLI IA). `/proc/<pid>/environ` et `mem` de l'agent restent lisibles par un plugin du même utilisateur, limités seulement par les contrôles ptrace. Mesure déjà prise : environnement minimal de l'enfant. Un plugin .NET n'a jamais été essayé sous Landlock réel.
- Proposition : espace de noms PID ou utilisateur dédié aux plugins ; tester Landlock réel (aucun scénario Landlock n'a été exécuté en conteneur de développement).

### 4. Pas de reprise après perte d'un serveur
- Constaté : par conception, une exécution reste sur le serveur qui l'a créée ; la perte de ce serveur n'est reprise par personne tant qu'il ne revient pas. Les baux ne sont pas répliqués ; seule la perte d'un **worker** est couverte (tentative échouée, relance explicite ou bornée).
- Impact direct sur l'objectif « reprise après perte d'un serveur ou d'un worker ». À valider avec Rémi : acceptable, ou besoin d'une reprise inter-serveurs (donc d'une forme d'exclusion plus forte, voir point 2).

## À décider (périmètre produit)

### 5. Relations entre clusters (ancien ADR 0038 §9, numéro 0042 réservé)
Transfert de nœuds entre clusters, partage de bibliothèques, propriété avec époque, garde-fous anti-doublon sans quorum : conçu, **jamais implémenté**. Garder comme feuille de route ou abandonner ?

### 6. Outils médias personnalisés / gérés (ancien ADR 0064)
Fournisseurs `custom`/`managed`, manifeste `tool.json`, `/opt/encoding/tools/`, test de conformité : **non implémenté** (seules la validation MCP et la liste des outils existent). `docs/operations/todo-hardware-accelerated-tools-policy.md` en dépend. Garder ou abandonner ?

### 7. Store de plugins : compléments
Déjà livré (PR #205). Reste : réplication de l'état entre serveurs (aujourd'hui local), signature des paquets, contrôle de `minEngineVersion`/`requiredTools`, désactivation sans retrait, refus de retrait d'un plugin utilisé, synchronisation périodique, adresse du dépôt officiel (non définie).

### 8. ~~Aucun déclencheur « après publication »~~ — traitée
Résolue par l'ADR [0003](adr/0003-publication-etape-de-flow.md) : le bloc `base-publish-result` publie au milieu du flow et les blocs Plex, Radarr, Sonarr, Jellyfin le suivent. `media.published` reste un événement d'audit. Reste : un runner réel n'a pas encore exécuté le bloc de bout en bout, et le modèle de flow livré (FileFlows V3) n'utilise pas encore ce bloc.

### 9. Compatibilité entre versions de nœuds
Seule la version de protocole (`Current = 1`) est contrôlée. Pas de plage de schéma, pas de règle « serveurs avant workers », pas de N/N-1 en mode Raft. Les migrations doivent rester additives en attendant.

## Hygiène du dépôt

- `backend/migrations/0036_library_cluster_tombstones.sql` (racine) n'est pas chargé par le migrateur (seul `migrations/sqlite/` l'est) : reliquat à supprimer.
- Les instructions du projet (mission) et la mémoire d'équipe citent encore PostgreSQL ; le code, `AGENTS.md` et la production (vérifiée le 2026-10-21) sont en SQLite seul. À aligner côté instructions du projet, hors dépôt.
- Documents d'exploitation : ré-audités le 2026-10-21 (index dans `docs/operations/README.md`). Restent à ré-auditer : `docs/design/*`.
- `backend/README.md` : sections datées par numéros de migration (« la migration 0008 ajoute… ») à réécrire en renvoyant vers les fiches.

## Non vérifié en conditions réelles (à ne pas présenter comme garanti)
Landlock en conteneur de production ; GPU réel (QSV, VAAPI, NVENC) ; Plex/Jellyfin/Sonarr/Radarr réels ; NAS/NFS (atomicité du renommage et des verrous) ; cluster multi-hôtes sous charge (changement de mode, reprise après perte de worker) ; thème sombre et mobile de la page Nœuds et de l'éditeur de flow.
