# 0043 — SQLite derrière une abstraction d'accès aux données

Statut : SQLite et PostgreSQL sont pris en charge. SQLite est le choix par défaut pour une installation neuve ; la décision de garder une base locale par serveur est précisée dans [0049](0049-multi-server-shared-cluster.md).

## Abstraction livrée

- Les dépôts utilisent les types ADO.NET génériques (`DbDataSource`, `DbConnection`, `DbCommand`). `Infrastructure/Data/Sql.cs` centralise les paramètres propres aux fournisseurs et l'identification SQLite/PostgreSQL.
- `Database:Provider` choisit explicitement le fournisseur. Sinon, `ConnectionStrings:Cluster` permet de le déduire : `Data Source=…` désigne SQLite ; `Host=…` désigne PostgreSQL. Le fichier `appsettings.json` et l'image Docker démarrent avec SQLite ; les déploiements PostgreSQL existants restent compatibles.
- `SqliteDataSource` active WAL, clés étrangères et délai d'attente d'écriture. Les transactions `BEGIN IMMEDIATE` sérialisent les écritures propres à un fichier. Les dates sont en texte UTC, les UUID en texte, les tableaux et JSON en JSON texte.
- Le traducteur SQLite adapte le SQL commun (horloge, intervalles, verrous, tableaux, JSON et alias de mise à jour). Les différences restantes sont traitées au point d'accès concerné avec `Sql.IsSqlite`.
- Les migrations PostgreSQL sont dans `migrations/`, celles de SQLite dans `migrations/sqlite/`. Le baseline SQLite 0033 représente le schéma PostgreSQL au moment du portage ; les changements ultérieurs ont des migrations parallèles.
- `Encoding.Api transfer-database` copie les données entre fournisseurs vers une cible vide, en conservant la source. Cette commande reste une migration/copie initiale ; elle ne fusionne pas des bases ayant évolué séparément.

## Stockage multi-serveur

Un fichier SQLite ne doit pas être partagé entre hôtes. Chaque serveur utilise son fichier local et réconcilie progressivement ses données avec les autres serveurs du cluster via le maillage 0049. Plusieurs processus sur le même fichier sont sérialisés par SQLite ; un déploiement peut aussi conserver PostgreSQL comme base commune.

Le maillage ajoute un journal séquencé et signé, ainsi qu'une projection LWW des membres, configurations, bibliothèques, flows, profils et états d'exécution durables. Les tables de baux et de réservations actives restent locales ; la liste précise est tenue dans 0049.

## Tests

Les scénarios pertinents s'exécutent sur les deux fournisseurs avec `ENCODING_TEST_PROVIDER=sqlite|postgres`. Les tests SQLite utilisent un fichier temporaire par scénario. Les migrations, le traducteur SQL, le runtime, la copie inter-fournisseurs et le maillage disposent de tests dédiés.
