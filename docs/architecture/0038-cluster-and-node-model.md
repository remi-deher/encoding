# 0038 — Modèle cluster et nœud : nœud autonome, rattachement, relations

Statut : acceptée par Rémi (2026-10-15) pour le modèle cluster/nœud (sections 1 à 7 et décisions ci-dessous), la synchronisation entre clusters (9) et l'évitement des doublons (9.1) ; le stockage (8) reste en discussion. Rien n'est encore implémenté.

Décisions de Rémi déjà prises : nœud autonome = cluster à un nœud (1) ; un nœud serveur+worker qui rejoint un cluster devient worker seul, son serveur local s’arrête (3, précisé en 0039) ; approbation à chaque rattachement et, entre clusters liés, des deux côtés (4). Le stockage (PostgreSQL ou SQLite) et la synchronisation entre clusters sont traités dans les sections 8 et 9.

## Correction du 2026-10-15 : nœud autonome, cluster, groupe

Rémi précise le vocabulaire (il remplace la recommandation du point 1 « cluster implicite visible ») :

- **Nœud autonome** : une installation seule, qui n'appartient à aucun cluster. L'interface et l'API l'appellent ainsi tant qu'aucun autre nœud ne dépend de lui. Techniquement l'identité de cluster interne (base, identifiant) peut subsister ; c'est un détail d'implémentation, pas un concept affiché.
- **Cluster** : unité de gestion qui regroupe des nœuds (état partagé, baux, bibliothèques, administration) ; frontière d'autorité et de cohérence. Créer un cluster, c'est promouvoir un nœud autonome ; y joindre un nœud l'y rattache.
- **Groupe de nœuds** : classement dans un cluster (0034), sans effet sur la gestion.
- **Groupe de clusters** : classement entre clusters, **reporté** (hors périmètre pour l'instant).

Mise en œuvre : la page Nœuds affiche « Nœud autonome » quand le cluster ne compte que ce nœud (`clusterNodeCount === 1`) et « Dans un cluster » sinon ; l'action de départ y est libellée « Réinitialiser le rattachement ». Les identifiants internes ne changent pas : pas de migration.

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

## 8. Stockage (en discussion)

Rémi souhaite à terme retirer la dépendance à PostgreSQL. Le code actuel en dépend fortement (37 fichiers, 281 commandes SQL, 31 migrations ; verrous `FOR UPDATE`, `SKIP LOCKED`, verrous consultatifs, `jsonb`, horloge du serveur). Galera (MariaDB) a été évalué : quorum de 3 nœuds, conflits de certification sur les baux, même réécriture que SQLite avec un serveur externe en plus. Direction recommandée : une couche de stockage commune, SQLite embarqué pour le nœud autonome et le cluster d'un seul serveur, une base externe interchangeable pour plusieurs serveurs. La décision reste à confirmer.

## 9. Synchronisation entre clusters (demande de Rémi)

Besoin : synchroniser les états et l'historique entre clusters, même à deux, sans quorum.

Principe proposé : **la cohérence forte reste à l'intérieur d'un cluster, la cohérence éventuelle entre clusters.**

- Les baux, réservations de médias et attributions de tâches ne sont jamais répliqués : ils n'ont de sens qu'au sein d'un cluster qui a un seul registre.
- Les **états et l'historique** (exécutions terminées, journaux, instantanés avant/après, définitions de bibliothèques et de flows publiés) sont répliqués de façon asynchrone, sans consensus.
- **Chaque enregistrement a un cluster propriétaire** (celui qui l'a produit) et n'est modifiable que là ; les autres clusters en tiennent une copie en lecture. Les conflits d'écriture disparaissent par construction. Les modifications partagées (par exemple une bibliothèque commune) ont un propriétaire unique à un instant donné, transférable avec approbation.
- Mécanisme : journal de changements append-only par cluster (identifiant d'origine, numéro de séquence), tiré par les clusters liés avec un curseur, application idempotente, reprise après coupure. Aucun quorum : chaque cluster fonctionne seul et rattrape son retard au retour du lien.
- Transport : HTTPS authentifié par la relation approuvée entre les deux clusters (4) ; indépendant du moteur de base de données, donc compatible avec PostgreSQL comme avec SQLite.

Limite à assumer : sans quorum, aucun mécanisme ne peut garantir à la fois la bascule automatique et l'absence de traitement en double après une coupure entre deux clusters. Deux traitements simultanés d'un même média restent sans danger pour les données tant que la source est préservée et que chaque exécution publie dans son propre dossier (0015, 0030) : ils coûtent du calcul, pas de l'intégrité. Recommandation : propriété d'une bibliothèque à un seul cluster à la fois, reprise par l'autre cluster sur action explicite ou sur délai configuré, avec le remplacement de la source désactivé dans les deux cas.

### 9.1 Éviter le traitement en double sans quorum

Sans quorum, l'exclusion stricte entre deux clusters coupés l'un de l'autre est impossible (compromis disponibilité / cohérence). On la remplace par trois garde-fous cumulables qui ramènent le risque à une fenêtre étroite, sans danger pour les données :

1. **Propriété par bibliothèque avec époque.** Une bibliothèque a un seul cluster propriétaire, qui seul planifie ses traitements. Un transfert ou une reprise incrémente une époque (numéro de génération) répliquée avec l'historique ; un cluster dont l'époque est dépassée cesse de planifier dès qu'il l'apprend. La reprise est explicite (administrateur) ou automatique après un silence configurable du propriétaire.
2. **Arbitre commun optionnel : le stockage des médias.** Si les deux clusters voient le même stockage (cas d'un NAS partagé), un marqueur atomique à côté du média (création exclusive, renouvelé comme un bail, contenant cluster, époque et expiration) fait office de témoin sans quorum : un seul cluster le détient. À valider sur NFS (création exclusive et renommage atomiques).
3. **Clé de travail idempotente.** Chaque traitement a une clé déterministe (empreinte de la source, version du flow, profil). Elle est répliquée dans l'historique ; avant de démarrer, un cluster vérifie qu'aucun résultat validé ni traitement en cours n'existe pour cette clé. Elle couvre tous les doublons non simultanés. Un doublon simultané ne peut subsister que pendant une coupure réelle entre clusters sans stockage commun ; il coûte du calcul, la source étant préservée et chaque exécution publiant dans son propre dossier.

Le remplacement de la source reste désactivé par défaut et ne doit jamais être activé sur une bibliothèque partagée entre clusters sans l'arbitre du point 2.

Précisions : la propriété d'une bibliothèque est **assignée** (à la création ou à l'import) et transférée par une cession explicite, jamais élue dynamiquement ; la cession suit toujours l'ordre « A se déclare cédant, puis B démarre », et si l'acquittement est perdu l'objet reste à A (échec sûr, sans reprise automatique par B). À l'intérieur d'un cluster, le jeton d'époque des baux (0015) refuse déjà les écritures d'un worker devenu obsolète.
