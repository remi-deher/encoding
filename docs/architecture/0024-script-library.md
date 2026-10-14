# 0024 — Bibliothèque de scripts

## Décision

Les scripts réutilisables sont des ressources partagées du cluster, gérées dans la section **Librairies**. La page **Scripts** permet de créer et modifier un brouillon JavaScript ou Python, puis de publier des versions immuables. PostgreSQL est la source de référence commune aux serveurs.

Un flow qui utilise un script conserve une référence `scriptId@version` à titre de provenance, ainsi qu’une copie du langage et du code dans sa configuration. Le moteur exécute cette copie. Une modification ultérieure du brouillon ou une nouvelle publication ne change donc pas un flow existant ; sa définition reste autonome pour les sauvegardes et les archives portables.

Le script choisi dans l’éditeur est copié dans la configuration du bloc. L’utilisateur peut ensuite le modifier pour ce flow sans altérer la ressource de la bibliothèque. La validation du flow vérifie le format de la référence, mais ne requiert pas une lecture de la bibliothèque lors de l’exécution.

## Versions et concurrence

- Chaque script possède un brouillon avec une révision croissante.
- L’enregistrement compare la révision attendue et retourne un conflit HTTP 409 si un autre utilisateur a modifié le brouillon.
- La publication compare aussi cette révision, puis crée la prochaine version dans une transaction PostgreSQL.
- Les instantanés publiés ne sont pas modifiables et restent disponibles pour être copiés dans de nouveaux flows.
- Les noms de brouillons sont uniques sans tenir compte de la casse.

## Exécution

Cette bibliothèque ne crée pas de nouveau moteur d’exécution. Les blocs de script continuent d’utiliser le runtime existant, ses limites de durée et l’isolation Linux Landlock documentée dans [0015](0015-flow-runtime-and-portable-archives.md). La page permet de stocker et éditer du code ; elle ne l’exécute pas.

## API

- `GET /api/v1/scripts` liste les scripts et leurs versions publiées.
- `POST /api/v1/scripts` crée un brouillon.
- `GET /api/v1/scripts/{id}` lit le brouillon et l’historique.
- `PUT /api/v1/scripts/{id}/draft` met à jour le brouillon avec contrôle de révision.
- `POST /api/v1/scripts/{id}/publish` publie le brouillon avec contrôle de révision.
- `GET /api/v1/scripts/{id}/versions/{version}` lit un instantané précis.

La migration `0018_script_library.sql` ajoute les tables du brouillon et des instantanés ; elle ne modifie aucun média ni aucune définition de flow existante.
