# 0001 — État partagé du cluster et présence des nœuds

- Statut : décision historique pour le premier socle ; le stockage multi-serveur est remplacé par [0049](0049-multi-server-shared-cluster.md)
- Date : 13 octobre 2026

## Décision

Les instances serveur sont des pairs sans autorité permanente. Chaque serveur utilise son fichier SQLite local et réconcilie éventuellement ses données de proche en proche, sans quorum. Le journal et les domaines réconciliables sont suivis dans 0049.

Le premier contrat backend versionné est `api/v1`. Un nœud publie périodiquement son identité, ses rôles et les capacités qu'il a observées. `lastSeenAt` est horodaté par le serveur, jamais fourni par le nœud. L'état en ligne/hors ligne est dérivé de cette date et du seuil de présence configuré, ce n'est pas un état persistant distinct.

Dans le modèle SQLite local, les nœuds, leurs réglages et leur présence sont réconciliés par le maillage décrit en 0049. L'identifiant stable du nœud reste sa clé ; les heartbeats fusionnent de façon monotone. Cela ne constitue pas le mécanisme de bail des tâches.

## Premier contrat

- `PUT /api/v1/nodes/{nodeId}/heartbeat` crée ou actualise un nœud. Répéter le même appel ne crée pas de doublon.
- `GET /api/v1/nodes` retourne l'état connu, les rôles, les capacités et l'heure du dernier signal.
- `GET /api/v1/nodes/{nodeId}` retourne le même contrat pour un nœud, ou `404`.
- Les capacités sont des déclarations observées, pas une preuve d'accès à une bibliothèque ni une autorisation d'exécuter une tâche.
- Les détails de bibliothèques, de charge et de file sont absents tant que leurs sous-systèmes ne les fournissent pas.

## Sécurité et limites

L'agent protège les heartbeats avec un jeton dans l'en-tête `X-Node-Token`, comparé en temps constant par l'API. Les empreintes des jetons sont répliquées sous chiffrement AEAD par le maillage. Compose lie l'API à la boucle locale ; tout déploiement exposé doit employer HTTPS et un contrôle d'accès adapté.

Les scripts SQL du dossier `backend/migrations` sont versionnés et appliqués dans une transaction au démarrage. Ils ne suppriment ni ne modifient des médias. La migration est additive et le schéma est partagé entre les instances.

## Conséquences

- Une panne du stockage local rend les mises à jour de présence de ce serveur indisponibles ; les autres serveurs continuent sur leurs propres bases.
- Chaque serveur peut répondre aux lectures et écrire les signaux de présence sans état local faisant autorité.
- Chaque serveur calcule la présence à partir de son horloge locale ; les horloges doivent rester synchronisées pour départager correctement les écritures réconciliées.
- Les clients affichent le contrat métier (`status`, `lastSeenAt`) sans reconstruire la règle de présence.
- L'attribution des tâches est locale au serveur qui les crée ; les baux actifs ne sont pas réconciliés. Une reprise exige une relance explicite selon 0015 et 0049.
