# 0046 — Rattachement en attente : abandon, expiration et adresses oubliables

Statut : implémentée (demande de Rémi : fiabiliser le parcours d’enrôlement). Complète [0040](0040-two-phase-join-and-verification-code.md) et [0045](0045-standalone-node-and-explicit-cluster.md).

## Constat

Un nœud autonome qui tentait de rejoindre un cluster enregistrait d’abord une identité « en attente » (cluster, jeton). Si l’enrôlement direct était refusé (fenêtre fermée) ou si le cluster était injoignable, seul un changement de cluster en cours était annulé : un nœud autonome restait bloqué sur un cluster qui ne l’avait jamais accepté. L’interface masquait alors ce cluster de la liste, n’affichait plus « Nœud autonome » et proposait « Quitter ce cluster ». Une clé de jumelage expirée n’était jamais nettoyée.

## Décision

- Un rattachement qui n’aboutit pas (refus, cluster injoignable, clé de jumelage expirée) est **abandonné** : pendant un changement de cluster, l’ancien rattachement est rétabli (0040) ; pour un nœud autonome, l’identité en attente est effacée et le nœud redevient autonome. L’identifiant du nœud est conservé, le cluster ne l’ayant jamais enregistré. La raison est conservée dans `switchFailure` jusqu’à la tentative suivante.
- La boucle d’envoi de signaux vérifie l’échéance d’une clé de jumelage publiée et l’abandonne si elle est dépassée sans approbation.
- Un nœud en attente d’approbation propose « Annuler la demande » (même opération que « Quitter »).
- Les clusters connus du nœud portent leur origine (`network`, `manual`, `configured`) et `reachable`. Une adresse ajoutée à la main s’oublie (`DELETE /api/v1/worker/clusters/{id}`, relayé par `DELETE /api/v1/local-node/clusters/{id}`, administrateur seulement) ; elle n’est alors plus mémorisée ni sondée. Un cluster détecté ou le serveur du conteneur ne s’oublie pas.

## Parcours dans le panneau « Ce nœud »

L’étape est dérivée de l’état (`unattached`, `pending`, `switching`, `member`). En attente, le panneau affiche le cluster visé, le code de vérification, la clé (copiable) et l’échéance, et masque la liste des clusters : on annule d’abord la demande. Les deux méthodes sont nommées pour ce qu’elles font : « Demander à rejoindre » (demande approuvée par un administrateur après comparaison du code, recommandée) et « Rejoindre directement » (exige la fenêtre d’enrôlement ouverte). L’approbation ou l’abandon survenus pendant l’attente sont annoncés par une notification.

## Impacts

Agent worker (`WorkerIdentityStore`, `WorkerEnrollmentService`, `WorkerClusterDiscovery`), relais du serveur, panneau « Ce nœud ». Le contrat `GET /api/v1/worker/clusters` gagne `source` et `reachable` (champs ajoutés). Aucune migration.

## Vérifications

`WorkerPendingJoinTests` : refus direct et cluster injoignable laissent le nœud autonome, clé expirée relue après redémarrage, oubli d’une adresse mémorisée.
