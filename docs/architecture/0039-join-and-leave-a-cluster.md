# 0039 — Rejoindre et quitter un cluster (phase P2 de 0038)

Statut : implémentée (phase P2). Les relations entre clusters et le partage de bibliothèques restent en P3.

## Décisions de Rémi (2026-10-15)

- Un nœud serveur + worker qui rejoint un autre cluster devient **worker seul** : son serveur local s'arrête.
- Un nœud n'appartient qu'à un cluster à la fois ; l'arrivée est toujours approuvée par le cluster d'accueil.

## Comportement

- **Nœud autonome = cluster à un nœud.** C'est l'état d'une installation de l'image unique : son nœud local est enrôlé auprès de son propre serveur (0037). Aucun état « sans cluster » n'est ajouté.
- **Lister.** Le worker liste les clusters visibles (DNS-SD, cluster configuré) ; le cluster actuel est exclu de la liste.
- **Rejoindre.** Deux chemins, déjà existants : enrôlement direct (fenêtre ouverte par un administrateur du cluster d'accueil) ou clé de jumelage approuvée par un administrateur. Le worker déjà rattaché peut les utiliser avec `leaveCurrent` : il quitte d'abord son cluster actuel, après confirmation dans la console.
- **Serveur local.** Quand l'arrivée est confirmée (enrôlement accepté, ou premier heartbeat réussi après approbation) et que le cluster d'accueil n'est pas le serveur du conteneur, l'agent demande au lanceur le mode worker seul. La base et les bibliothèques du serveur local ne sont ni modifiées ni supprimées : elles sont simplement hors service tant que ce serveur est arrêté, donc inutilisées par le cluster d'accueil (« désactivées à l'arrivée »).
- **Quitter.** `POST /api/v1/worker/leave` côté agent. L'agent prévient son cluster par `POST /api/v1/nodes/{id}/leave` (jeton du nœud ; même retrait que la suppression par un administrateur, mêmes refus si un traitement est en cours : 409), efface son identité et repart d'un nouvel identifiant, car une identité retirée ne se réactive jamais (0017). Si le lanceur gère le nœud et que le serveur local est arrêté, il est relancé : le nœud redevient autonome et retrouve ses bibliothèques. Un cluster injoignable n'empêche pas de partir ; l'administrateur retire la ligne hors ligne.
- **Export.** `GET /api/v1/libraries/export` (session requise, lecture seule) fournit les définitions de bibliothèques en JSON (format 1, sans chemins d'accès de nœud) avant de quitter. L'import dans un autre cluster n'est pas fourni en P2.

## Sécurité

- La route `leave` est anonyme au sens de la session (comme le heartbeat), authentifiée par le jeton du nœud lui-même et limitée en débit ; le test qui énumère les routes anonymes est mis à jour. Le jeton d'un autre nœud est refusé.
- Le secret du conteneur n'est toujours présenté qu'au serveur local en boucle locale.

## Limites connues

- Aucune base n'est migrée ni fusionnée ; l'historique d'un nœud reste dans le cluster qu'il quitte.
- Un nœud en attente d'approbation (jumelage non confirmé) peut annuler en quittant ; il retrouve alors son état autonome.
- Les bibliothèques d'un serveur arrêté ne sont pas visibles depuis le cluster d'accueil ; l'export sert à les réutiliser à la main.
