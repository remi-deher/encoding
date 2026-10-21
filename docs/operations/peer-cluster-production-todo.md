# Cluster pair à pair — suivi de préparation

> **Statut : aperçu expérimental.** « Terminé » ci-dessous signifie « codé et testé en CI », pas « validé en production » : le mode est désactivé par défaut et son avenir est une décision en attente ([open-questions](../architecture/open-questions.md)).

Les six points sont traités dans le code et les procédures. Voir [0061](../architecture/domains/replication-storage.md) et le [guide d’exploitation](peer-cluster-preview.md).

| Point | État | Réalisation et validation |
| --- | --- | --- |
| 1. État métier partagé | [x] Terminé | Réplication transactionnelle de toutes les tables métier ; imports de flows atomiques ; mutations reçues sur un follower transmises au leader. Comptes et sessions restent locaux. |
| 2. Orchestration durable | [x] Terminé | Tâches, checkpoints, baux, réservations et résultats répliqués. Époques incluant le terme Raft ; ancien worker refusé après élection ; effets externes incertains sans répétition automatique. |
| 3. Membership et reprise | [x] Terminé | Approbation explicite, ajout/retrait engagés, refus de fusion de groupes indépendants, rattrapage et restauration d’un pair avec sa propre identité. |
| 4. Migration de l’autorité | [x] Terminé | Copie cohérente d’une base SQLite vers une cible vide, source intacte, vérification des compteurs, ordre des références et refus de fusion. |
| 5. Stockage et accès | [x] Terminé | Nœuds de traitement choisis par bibliothèque, accès direct avec chemin propre ou transfert WebDAV via une source déclarée. Médias externes à Raft. Source indisponible/transfert échoué : échec immédiat et réservation libérée. |
| 6. Exploitation et mises à jour | [x] Terminé | Partitions, majorité/minorité, anciens baux, redémarrages, rejeu du WAL, restauration d’un pair et des trois pairs, rotation des certificats, refus des versions incompatibles, métriques et alertes. |

## Conditions d’installation

- Trois pairs votants, bases et volumes Raft indépendants, fournisseur et version identiques ; une seule autorité lors de l’import initial.
- Même clé `SharedDataKey` sur les pairs, secrets d’identité distincts et sauvegardés ; aucun secret dans Git.
- Stockages des médias, artefacts et sauvegardes de flows configurés séparément. La réplication SQL ne copie aucun fichier média.
- Charger les règles Prometheus, adapter les seuils et vérifier les sondes de chaque pair.
- Mises à jour coordonnées ; N/N-1 et migrations de schéma en ligne ne sont pas pris en charge. Une nouvelle version incompatible nécessite sa procédure de migration hors ligne validée avant déploiement.
- Limite initiale de 64 Mio pour une transaction ou l’image d’import initiale. Les opérations trop volumineuses échouent explicitement.

## Preuves reproductibles

La CI utilise SQLite pour toutes les bases. La validation locale du 2026-10-17 compile la solution Release avec `--warnaserror` (0 avertissement, 0 erreur) et passe les prototypes de maillage et de consensus, les 37 tests Python, les 123 tests runtime SQLite avec les outils médias, ainsi que les scénarios du lanceur et des plugins. `promtool` valide les sept alertes et les quatre scénarios associés.

`PeerClusterIntegrationTests` utilise trois processus API, des volumes temporaires séparés et des partitions bidirectionnelles réelles du transport TLS. Il vérifie les lectures convergentes après reconnexion, la poursuite des écritures majoritaires, le refus de l’ancien leader, le rejet d’un ancien bail, la perte du quorum, les rotations et les restaurations. Une écriture après le dernier instantané doit survivre à la restauration complète par rejeu du WAL.

`PeerDatabaseReplicationTests`, `PeerOperationsTests`, le transfert SQLite et les scénarios runtime complètent les essais. Les règles d’alerte sont fournies dans `peer-cluster-alerts.yml` et vérifiées par `promtool`, avec quatre scénarios (quorum disponible/perdu, journal inactif/bloqué). Ce contrôle est intégré à la CI.

## Évolutions hors périmètre de cette livraison

Mises à jour progressives N/N-1, migration en ligne du schéma, imports initiaux supérieurs à 64 Mio et découverte automatique des adresses. Ces évolutions ne sont pas nécessaires au fonctionnement documenté à trois pairs de même version. Les outils de stockage restent responsables de la redondance des fichiers.
