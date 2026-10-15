# 0043 — SQLite derrière une abstraction d'accès aux données

Statut : étapes 1 et 2 implémentées (accès génériques ; dialecte et fournisseur SQLite, PostgreSQL inchangé) ; étape 3 à venir. Portée validée par Rémi le 2026-10-15 : SQLite pour les clusters à un seul serveur. Décision de Rémi du 2026-10-15 : « mets SQLite avec une abstraction de donnée ». Cela lève l'interdit de AGENTS.md de ne pas remplacer PostgreSQL sans décision explicite ; PostgreSQL reste pris en charge. Le point 9 de 0038-0041 (stockage) est donc rouvert.

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

1. **Fait.** Les dépôts et services utilisent les types ADO.NET génériques (`DbDataSource`, `DbConnection`, `DbCommand`…). `Infrastructure/Data/Sql.cs` est le seul fichier (avec `Program.cs` pour l'enregistrement) qui connaît Npgsql : fabrique de commandes, paramètres typés (`SqlKind` : texte, JSON, binaire, entier, décimal, uuid, tableau de textes) et détection d'une violation d'unicité. Un test (`DataAccessBoundaryTests`) interdit toute nouvelle référence à Npgsql ailleurs. Le SQL des dépôts reste celui de PostgreSQL : le dialecte (horloge, upsert, verrous, JSON) est l'objet de l'étape 2.
2. **Fait.** `Database:Provider` (`postgres` par défaut, `sqlite`) choisit le fournisseur (`Infrastructure/Data/DatabaseProvider.cs`). Le SQL des dépôts reste celui de PostgreSQL : `Infrastructure/Data/Sqlite/SqliteTranslator.cs` le traduit à l'exécution (instants, intervalles, verrous de ligne et consultatifs, tableaux et JSON, alias de `UPDATE`, `repeat`, égalité d'ensembles `<@`). `SqliteDatabase.cs` enveloppe `Microsoft.Data.Sqlite` derrière `DbDataSource` : pragmas (WAL, clés étrangères, délai d'occupation), transactions `BEGIN IMMEDIATE` (écrivain unique, ce qui remplace `FOR UPDATE`/`SKIP LOCKED`), conversion des paramètres. Les instants sont du texte UTC de largeur fixe, l'infini est `9999-12-31 23:59:59.999999Z`, les identifiants sont des textes minuscules, les tableaux et le JSON sont du texte JSON. Les migrations SQLite sont dans `migrations/sqlite/` (base `0033_baseline.sql` générée depuis le catalogue PostgreSQL, avec les triggers d'immutabilité et la donnée initiale de l'identité de cluster). Quand une requête n'a pas d'équivalent traduisible (`jsonb_array_elements`, `@>`, expression de table avec écriture), le dépôt choisit son SQL avec `Sql.IsSqlite`. Lectures scalaires portables : `ScalarBoolAsync`, `ScalarInt32Async`, `ScalarGuidAsync`, `ScalarInstantAsync`, `GetStringArray`. La suite de garanties tourne sur les deux fournisseurs (`ENCODING_TEST_PROVIDER=sqlite|postgres`).
3. Tests de garanties sur les deux fournisseurs, export/import entre modes, image Docker sans PostgreSQL embarqué par défaut.

## Risques

- Le portage touche presque tout le backend : il faut le faire par étapes et en rebasant souvent, car d'autres fils modifient les mêmes dépôts.
- Les colonnes générées et `jsonb` n'ont pas d'équivalent exact en SQLite ; chaque cas doit être traité et testé.
