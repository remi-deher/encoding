# 0043 — SQLite derrière une abstraction d'accès aux données

Statut : proposée. Décision de Rémi du 2026-10-15 : « mets SQLite avec une abstraction de donnée ». Cela lève l'interdit de AGENTS.md de ne pas remplacer PostgreSQL sans décision explicite ; PostgreSQL reste pris en charge. Le point 9 de 0038-0041 (stockage) est donc rouvert.

## État des lieux (mesuré dans le code)

- 38 fichiers C# ouvrent directement des connexions Npgsql avec du SQL écrit à la main ; il n'y a pas d'ORM ni de couche commune.
- 33 migrations SQL écrites pour PostgreSQL. Usages propres à PostgreSQL : `jsonb` (117), `timestamptz` (203), `clock_timestamp()` (203), `uuid` (222), `interval` (16), colonnes générées (2), `FOR UPDATE SKIP LOCKED` (1), verrous consultatifs `pg_advisory*` (4 : migrations, installation initiale, jumelage).
- Les garanties du cluster (baux avec époque, réservations uniques par média, verrouillage des tâches) reposent sur des transactions PostgreSQL.

## Limite de fond

SQLite est un fichier à un seul écrivain. Deux serveurs sur deux hôtes ne peuvent pas le partager sans le corrompre. Conséquence assumée :

- **Mode SQLite** : un cluster qui n'a qu'un seul serveur (cas par défaut de l'image unique, nœud autonome). Les workers restent distants et parlent à ce serveur par l'API, comme aujourd'hui.
- **Mode PostgreSQL** : obligatoire dès qu'un cluster a plusieurs serveurs actifs. La haute disponibilité de la base reste de la responsabilité de l'exploitation (point 6, 0041).
- Le passage d'un mode à l'autre est une migration explicite (export/import), jamais silencieuse. Un second serveur refuse de rejoindre un cluster en mode SQLite et le dit.

## Proposition d'abstraction

1. Une seule interface d'accès (`IDatabase`) fournit : ouverture de connexion et de transaction, et un dialecte qui nomme les différences (horodatage courant, upsert, JSON, verrou applicatif, réservation d'une ligne de file). Les dépôts n'importent plus Npgsql.
2. Deux fournisseurs : Npgsql (existant) et Microsoft.Data.Sqlite. En SQLite : mode WAL, `BEGIN IMMEDIATE` pour les sections critiques (équivalent du verrou consultatif et de `SKIP LOCKED`, valable car un seul écrivain), dates en texte UTC ISO 8601, JSON en texte, UUID en texte.
3. Migrations par fournisseur (`migrations/postgres/`, `migrations/sqlite/`) avec le même numéro et la même empreinte logique ; le chargeur vérifie que les deux jeux sont alignés.
4. Les tests de cluster (baux, réservations, reprise, états de nœud) tournent sur les deux fournisseurs en CI ; une garantie qui ne passe que sur l'un des deux n'est pas livrée.

## Plan par étapes (une PR chacune)

1. Interface, dialecte et fournisseur PostgreSQL ; portage mécanique des 38 fichiers, sans changement de comportement (tests existants inchangés).
2. Fournisseur SQLite, jeu de migrations SQLite, sélection par configuration ; le mode se choisit à l'installation.
3. Tests de garanties sur les deux fournisseurs, export/import entre modes, image Docker sans PostgreSQL embarqué par défaut.

## Risques

- Le portage touche presque tout le backend : il faut le faire par étapes et en rebasant souvent, car d'autres fils modifient les mêmes dépôts.
- Les colonnes générées et `jsonb` n'ont pas d'équivalent exact en SQLite ; chaque cas doit être traité et testé.
