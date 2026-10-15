# 0038 — Modèle cluster et nœud : nœud autonome, rattachement, relations

Statut : proposition (plan de Rémi, 2026-10-15). Aucune implémentation tant que les points ouverts ne sont pas tranchés.

## Plan proposé par Rémi

1. On peut créer un cluster depuis un nœud qui n'appartient à aucun cluster (nœud **autonome**).
2. Tout nœud autonome peut lister les clusters et en rejoindre un.
3. Un nœud n'appartient qu'à un seul cluster à la fois, mais il peut être basculé automatiquement.
4. Des **relations** entre clusters permettent de transférer des nœuds d'un cluster à l'autre, avec approbation.
5. Chaque cluster gère ses propres bibliothèques ; les relations permettent de les partager entre clusters.
6. Un nœud autonome qui a déjà des bibliothèques les voit désactivées en rejoignant un cluster : il prend les bibliothèques du cluster par défaut, et peut exporter les siennes.

## Situation actuelle (code)

- Un cluster est une base PostgreSQL avec ses volumes d'artefacts, identifiée par un identifiant propre (`/api/v1/cluster`). Un serveur de plus rejoint en pointant vers la même base.
- Un nœud porte une identité locale (`node-identity.json` : id, jeton, cluster) et des rôles serveur / worker. Un worker rejoint un cluster par enrôlement (fenêtre, clé de jumelage approuvée, acceptation automatique) ; il le trouve par DNS-SD ou par `Cluster__ApiBaseUrl`.
- Avec l'image unique (0036, 0037), le nœud local est enrôlé d'office à son propre serveur. Un nœud est rattaché à un seul cluster ; en changer exige de réinitialiser son identité.
- Les bibliothèques sont des définitions du cluster ; chaque nœud déclare son accès (direct, non pris en charge, transfert).

## Points à trancher

1. **Nœud autonome : cluster implicite à un nœud, ou état « sans cluster » distinct ?** Recommandation : cluster implicite à un nœud, créé à l'installation avec le nom convivial choisi. Pas d'état spécial, même code que pour tout cluster ; « autonome » signifie alors « seul dans son cluster », et « rejoindre » fusionne ce nœud dans un autre cluster.
2. **Où vit l'état d'un cluster quand un nœud le quitte ?** La base PostgreSQL n'appartient pas à un nœud : un nœud qui quitte laisse sa base (archivée, jamais supprimée sans action explicite). À définir : qui héberge PostgreSQL d'un cluster à plusieurs nœuds (externe, ou porté par un nœud désigné), sans réintroduire de coordinateur central permanent (AGENTS.md).
3. **Rôles au rattachement.** Un nœud serveur + worker qui en rejoint un autre : devient-il worker seul (son serveur local s'éteint), ou serveur du cluster d'accueil (connecté à sa base) ? Le premier est un jumelage ; le second demande les accès à la base distante.
4. **Basculer « automatiquement ».** Qui déclenche, avec quelle autorisation ? Recommandation : jamais sans approbation du cluster d'accueil ; pour un transfert entre clusters liés, approbation des deux côtés. Les baux et tâches en cours sont drainés avant la bascule (reprise standard).
5. **Bibliothèques au rattachement.** Les siennes sont désactivées (pas supprimées) et exportables ; collisions d'identifiants et de chemins à traiter ; les chemins médias doivent exister de façon cohérente sur les nœuds du cluster d'accueil (accès direct ou transfert).
6. **Relations entre clusters : deux fonctions distinctes.** Le transfert de nœuds approuvé des deux côtés ; le partage de bibliothèques. Pour ce dernier, choisir entre : (a) les nœuds du cluster B peuvent servir de workers pour une bibliothèque de A, A restant propriétaire des tâches et du registre, ou (b) réplication de la définition de bibliothèque. Recommandation : (a), qui préserve « une seule tâche à la fois sur un média » et un seul registre par bibliothèque.
7. **Découverte et approbation.** Lister = clusters annoncés sur le réseau (DNS-SD) + saisie d'une URL. Rejoindre reste soumis à approbation du cluster d'accueil (clé de jumelage ou acceptation par un administrateur).

## Phasage proposé

- **P1** (fait, 0037) : nœud local enrôlé d'office, nom convivial à l'installation.
- **P2** : lister, rejoindre et quitter un cluster avec approbation ; nœud autonome = cluster à un nœud ; bibliothèques désactivées à l'arrivée et export.
- **P3** : relations entre clusters, transfert de nœuds approuvé des deux côtés, partage de bibliothèques.

## Garanties à conserver

Une seule tâche à la fois sur un média, baux et reprise après perte d'un nœud, sortie temporaire validée avant publication, routes fermées par défaut, retrait d'un nœud sans réactivation implicite (0017, 0031), secrets jamais envoyés à un autre cluster que celui qu'un administrateur a choisi.
