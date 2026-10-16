# 0001 — État partagé du cluster et présence des nœuds

- Statut : accepté pour le premier socle backend ; cible distribuée remplacée par [0060](0060-peer-replicated-cluster-state.md)
- Date : 13 octobre 2026

## Décision

Les instances serveur sont des pairs sans autorité permanente. Elles lisent et modifient l'état partagé dans une même base PostgreSQL, qui constitue la source de vérité logique du cluster. PostgreSQL devra être exploité en haute disponibilité pour éviter qu'il devienne un point de panne unique ; la topologie et le mécanisme de bascule restent à décider avant le déploiement de production.

Le premier contrat backend versionné est `api/v1`. Un nœud publie périodiquement son identité, ses rôles et les capacités qu'il a observées. `lastSeenAt` est horodaté par le serveur, jamais fourni par le nœud. L'état en ligne/hors ligne est dérivé de cette date et du seuil de présence configuré, ce n'est pas un état persistant distinct.

La table des nœuds est partagée par tous les serveurs. L'identifiant stable du nœud est sa clé primaire et l'upsert d'un signal de présence est atomique et répétable. Cela ne constitue pas encore le mécanisme d'élection ou de bail des tâches ; ces garanties seront spécifiées avec le répartiteur.

## Premier contrat

- `PUT /api/v1/nodes/{nodeId}/heartbeat` crée ou actualise un nœud. Répéter le même appel ne crée pas de doublon.
- `GET /api/v1/nodes` retourne l'état connu, les rôles, les capacités et l'heure du dernier signal.
- `GET /api/v1/nodes/{nodeId}` retourne le même contrat pour un nœud, ou `404`.
- Les capacités sont des déclarations observées, pas une preuve d'accès à une bibliothèque ni une autorisation d'exécuter une tâche.
- Les détails de bibliothèques, de charge et de file sont absents tant que leurs sous-systèmes ne les fournissent pas.

## Sécurité et limites

L'agent protège les heartbeats avec un jeton partagé dans l'en-tête `X-Node-Token`, comparé en temps constant par l'API. L'identité stable et le secret sont configurés par installation. L'enrôlement et la rotation des jetons, l'authentification utilisateur des lectures et le chiffrement du trafic inter-nœuds restent à définir. Compose lie l'API à la boucle locale ; tout déploiement hors de cette configuration doit employer HTTPS et un contrôle d'accès adapté.

Les scripts SQL du dossier `backend/migrations` sont versionnés et appliqués dans une transaction au démarrage. Ils ne suppriment ni ne modifient des médias. La migration est additive et le schéma est partagé entre les instances.

## Conséquences

- Une panne de PostgreSQL rend les mises à jour de présence indisponibles, même si plusieurs serveurs applicatifs tournent.
- Chaque serveur peut répondre aux lectures et écrire les signaux de présence sans état local faisant autorité.
- PostgreSQL calcule l'état de présence depuis son horloge afin que les serveurs API ne divergent pas à cause d'horloges locales différentes.
- Les clients affichent le contrat métier (`status`, `lastSeenAt`) sans reconstruire la règle de présence.
- L'attribution exclusive des tâches, les baux récupérables, l'enrôlement et la rotation des secrets, et la topologie HA PostgreSQL restent des décisions et livrables distincts.
