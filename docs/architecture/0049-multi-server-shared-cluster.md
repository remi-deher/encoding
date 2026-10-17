# 0049 — SQLite local et réconciliation maillée

Statut : transport et première couverture des données partagées implémentés. Chaque serveur garde une base locale et peut servir l’interface de gestion du cluster.

## Décision

Chaque serveur utilise sa base SQLite locale par défaut. Les serveurs d'un même cluster se découvrent par DNS-SD/mDNS et échangent un journal append-only de proche en proche. Aucun serveur permanent, quorum ou Raft n'est nécessaire. PostgreSQL reste pris en charge, notamment pour les installations utilisant une base commune.

Après approbation du rattachement, l’agent local adopte l’identifiant du cluster d’accueil. L’annonce mDNS est actualisée, puis le serveur conserve son API et réconcilie ses données avec les pairs. Les écritures sont faites sur le serveur ouvert par l’administrateur ; les pairs les reçoivent au prochain échange. La convergence est éventuelle, pas synchrone.

## Transport et conflits

- La migration 0035 ajoute l’identité de l’origine, le journal séquencé, les registres, les versions d’entités, les curseurs et les projections en attente.
- `POST /api/v1/cluster/reconciliation/exchange` échange des lots bornés. Les événements conservent leur origine lorsqu’ils sont relayés ; `(origine, séquence)` élimine les doublons.
- Les pairs sont découverts par mDNS quand ils annoncent le même identifiant de cluster. `Cluster:Reconciliation:Peers` accepte aussi des URL explicites.
- `Cluster:Reconciliation:SharedSecret` doit être identique sur les serveurs et contenir au moins 32 octets. HMAC authentifie les messages ; AES-GCM protège dans le journal et sur le transport les empreintes de jetons de nœuds, les hachages des codes et jetons de jumelage, les hachages de mot de passe et les valeurs de configuration des plugins. Les configurations de plugins sont déchiffrées puis rechiffrées avec le trousseau Data Protection local de chaque serveur.
- Les modifications sont arbitrées par entité : l’horodatage UTC gagne, puis l’identifiant d’origine et le numéro de séquence départagent une égalité. Les suppressions sont des tombstones. Les serveurs doivent garder leurs horloges synchronisées.
- Un événement reçu est journalisé et relayé même si une clé étrangère n’est pas encore disponible ; sa projection est retentée après réception des dépendances.

## Données réconciliées

Les écritures des tables ci-dessous sont détectées par empreinte de ligne, converties en événements et appliquées de façon idempotente. Les tables à historique important utilisent un curseur ; l’inventaire média n’est rescanné qu’à la publication d’un scan terminé.

- Identité et nom du cluster ; membres, rôles, capacités, présence, état de retrait, groupes et appartenances.
- Utilisateurs du cluster, réglages de rattachement et demandes de jumelage en attente. Une demande approuvée ou supprimée sur un serveur disparaît des autres après convergence. Les sessions restent propres à chaque serveur ; un compte se connecte localement sur le serveur qu’il utilise.
- Bibliothèques, chemins et accès par nœud, limites, paramètres de traitement, politiques de remplacement, dernier scan terminé et inventaire média.
- Flows, brouillons, historique des brouillons et versions publiées ; scripts et profils d'encodage ; templates et packs de composants. Les travaux de sauvegarde sont recréés localement à partir des définitions, et leur statut indique l'état du stockage de chaque serveur.
- Configurations globales et dérogations de plugins, y compris les secrets, protégés avec le secret partagé du maillage puis rechiffrés localement.
- État durable des exécutions, événements, étapes, checkpoints, métadonnées d'artefacts et reçus de traitement. Les reçus sont identifiés par le chemin du média afin de rester cohérents même si deux bases SQLite ont attribué des identifiants média différents.

La dernière écriture d’une même entité gagne. Deux créations isolées avec le même nom restent soumises aux contraintes uniques de la base : la projection qui entre en conflit est conservée en attente jusqu’à résolution de la collision. Une fois le réseau rétabli, les modifications indépendantes convergent sans verrou global.

## Données locales au serveur ou au worker

Les baux et réservations d'exécution, `execution_work`, les attentes actives, les scans en cours, les files de déclenchement, les sessions utilisateurs, les opérations de remplacement de source et les instantanés de travail d’un worker ne sont pas copiés. Les métadonnées d’artefacts sont propagées, pas les fichiers binaires eux-mêmes. Les fichiers de sauvegarde JSON des flows sont régénérés par chaque serveur depuis les définitions réconciliées ; leurs statuts restent locaux. Une exécution reste attribuée à un worker ; le maillage ne la reprend pas automatiquement ailleurs. Un nouveau traitement vient d’une règle explicite du flow ou d’une action manuelle.

L’inventaire média est réconcilié à la fin d’un scan. Les baux et l’état d’un scan actif restent sur le serveur qui l’exécute. La bibliothèque doit avoir des chemins configurés pour chaque worker qui doit traiter ces fichiers.

## Gestion depuis chaque serveur

Les routes de gestion restent servies par l’API locale. Après rattachement, la page Nœuds ne remplace plus le cluster par un lien vers le serveur d’accueil : les listes, réglages et actions sont affichés localement, puis diffusés par le maillage. Un nœud isolé peut donc lire et écrire ses données locales ; les autres serveurs voient les changements après reconnexion. Les baux, les processus métier actifs et les secrets de session d’authentification ne deviennent pas des états partagés.

La convergence n’est pas une transaction globale : deux serveurs isolés peuvent accepter des écritures concurrentes, puis appliquer la règle LWW au retour du lien. Aucun quorum ni garantie d’exclusion de travail entre bases indépendantes n’est revendiqué.

## Limites opérationnelles

- Ne pas partager un même fichier SQLite entre hôtes ni le monter comme base distribuée sur un stockage réseau.
- Les données ordinaires de réconciliation restent lisibles sur le réseau ; HTTPS est recommandé pour les URL de pairs. Les champs sensibles protégés par AES-GCM restent chiffrés même avec une URL HTTP.
- Les fichiers médias et les artefacts doivent être accessibles séparément depuis les workers concernés. La réplication de leurs métadonnées ne copie pas leur contenu.
- `transfer-database` reste une copie initiale vers une base vide ; il ne fusionne pas deux bases existantes.

## Vérifications

Les tests couvrent le relais, les doublons, le départage LWW, le rattachement à une nouvelle identité, la réplication de configurations et d’historiques sur deux bases SQLite, la protection des secrets plugin, le remappage des reçus de traitement sur SQLite et PostgreSQL, la création locale des tâches de sauvegarde et la non-réplication des baux actifs.
