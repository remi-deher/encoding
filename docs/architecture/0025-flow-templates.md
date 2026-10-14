# 0025 — Modèles de flow

## Décision

Un modèle de flow est une copie nommée d’une version publiée précise d’un flow. Il conserve la définition complète, les références de composants, leurs configurations et les connexions. La création du modèle ne modifie pas le flow source.

La conversion reçoit l’identifiant du flow et sa version. Le serveur copie cette version depuis `flow_versions` dans la même transaction que la création du modèle. Une version publiée est immuable ; un brouillon ne peut donc pas être converti directement. Un flow avec un brouillon en cours peut toutefois fournir sa dernière version publiée.

Les modèles sont stockés dans PostgreSQL avec leur nom, leur description, leur média et leur provenance. Les noms sont uniques sans tenir compte de la casse. Les modèles sont en lecture seule après leur création pour préserver leur lien avec la définition source.

Créer un flow depuis un modèle charge sa définition dans l’éditeur sous un nouvel identifiant de flow et avec la version initiale. Le nom proposé est une copie du nom du modèle et reste modifiable dans le parcours de création. Les composants absents du catalogue gardent leur référence et leur configuration, comme lors du chargement d’un flow enregistré ; la validation habituelle décide ensuite si le nouveau flow peut être publié.

## API

- `GET /api/v1/templates` liste les modèles.
- `GET /api/v1/templates/{id}` retourne les métadonnées et la définition.
- `POST /api/v1/templates/from-flow` crée un modèle à partir d’un identifiant de flow et d’une version publiée précise.

La migration `0019_flow_templates.sql` ajoute les modèles sans modifier les flows existants ni leurs versions.
