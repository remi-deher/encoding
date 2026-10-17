# 0043 — SQLite local par serveur

Statut : SQLite est le seul moteur de stockage de l’application.

## Utilisation

- Chaque serveur ouvre son propre fichier `cluster.db` dans son volume de données. Ne jamais partager le fichier entre hôtes ni le placer sur un stockage réseau.
- `SqliteDataSource` active WAL, les clés étrangères et un délai d’attente d’écriture. Les transactions d’écriture immédiate sérialisent les changements du fichier local ; les serveurs distincts convergent par le maillage 0049, sans transaction globale.
- Les dates sont stockées en texte UTC de largeur fixe, les UUID en texte et les tableaux/JSON en texte JSON. L’adaptateur SQL convertit les constructions utilisées par les dépôts vers les fonctions SQLite.
- Les migrations exécutées par le serveur se trouvent dans `backend/migrations/sqlite/`. Le baseline 0033 crée le schéma initial et les migrations suivantes font évoluer ce schéma. Les fichiers SQLite sont copiés dans l’image lors de la publication.
- La configuration de connexion accepte uniquement SQLite. Sans configuration, le serveur utilise `Data Source=./data/cluster.db`.

## Installation fraîche

Au premier démarrage, le serveur crée le fichier SQLite, applique toutes les migrations et crée une identité de cluster locale. Chaque serveur doit avoir son propre volume de données. Pour recommencer avec une base vide, utiliser un volume neuf ; cela crée une nouvelle identité locale et ne restaure aucun état métier.

## Cluster

Chaque serveur conserve les données locales nécessaires à son travail et réconcilie les données partagées avec les autres serveurs selon 0049. Un nœud isolé continue de lire et modifier sa copie ; la convergence est éventuelle. Les baux, réservations actives, sessions, files locales et fichiers binaires ne sont pas répliqués comme des lignes SQLite.

## Vérification

La suite exécute les migrations, dépôts, runtime, sauvegardes, scénarios d’API et maillage sur des fichiers SQLite temporaires. Les tests de cluster utilisent deux bases distinctes pour vérifier le transport et la réconciliation. Aucun serveur de base externe n’est requis.
