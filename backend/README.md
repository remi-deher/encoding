# Backend

Socle API ASP.NET Core sur .NET 10 et PostgreSQL partagé. L’API et l’agent échangent les contrats versionnés du projet `Encoding.Contracts`. Les décisions d’architecture sont décrites dans [`../docs/architecture/`](../docs/architecture/).

## Développement local

Prérequis : Docker Compose. Créer le fichier d’environnement local puis démarrer les services :

```powershell
Copy-Item .env.example .env
docker compose up --build
```

L’interface Angular et l’API sont servies par le même conteneur : ouvrir `http://localhost:4201` pour l’application ou `http://localhost:5080` pour l’API. La console locale de l’agent reste disponible sur `http://localhost:5081` et PostgreSQL sur `localhost:5432`. Depuis le poste worker, ouvrir l’interface Angular à `/worker`. Le navigateur appelle `localhost:5081` sur le poste où il s’exécute ; les origines autorisées se règlent avec `WORKER_UI_ORIGINS`. Les données d’identité du worker sont persistées dans le volume `encoding-node-data`.

Les migrations SQL versionnées s’appliquent au démarrage, sous verrou PostgreSQL. Pour ne lancer que la base : `docker compose up -d postgres`. Les identifiants PostgreSQL Compose sont réservés au développement local.

Le worker découvre les clusters annoncés en DNS-SD/mDNS (`_encoding._tcp`). Le service prend aussi en charge une adresse bootstrap `Cluster:ApiBaseUrl`, utilisée ici pour la découverte dans le réseau Compose où le multicast peut être indisponible. Pour un worker sur un autre hôte, définissez `CLUSTER_ADVERTISED_API_BASE_URL` avec une URL HTTPS joignable depuis ces machines, puis publiez le port API sur l’interface réseau prévue et configurez les origines navigateur. Le réglage de développement lie volontairement l’API à la boucle locale.

## Enrôlement worker

1. Démarrer l’agent sur la machine worker.
2. Ouvrir `/worker` depuis le navigateur de cette même machine.
3. Dans l’onglet **Nœud**, sélectionner le cluster détecté puis confirmer **Rejoindre**.

Autre parcours : cliquer sur **Générer une clé** dans la console worker, puis ouvrir **Ajouter un nœud** dans le catalogue du serveur et saisir la clé. Elle expire après 10 minutes et ne peut servir qu’une fois. Le worker attend la confirmation sur son heartbeat avant de se déclarer enrôlé.

L’acceptation automatique se règle sur le worker avec `Worker__AutoAcceptEnrollment` (Compose : `WORKER_AUTO_ACCEPT_ENROLLMENT`). Elle est désactivée par défaut. Lorsqu’elle est activée, le worker accepte automatiquement le seul cluster détecté ; s’il en voit plusieurs, il attend un choix explicite.

L’identité et le jeton aléatoire sont créés par l’agent, puis conservés dans son répertoire de données. Le serveur stocke uniquement le SHA-256 du jeton et l’exige pour les heartbeats via `X-Node-Token`. Les répétitions d’un enrôlement avec la même identité et le même jeton sont idempotentes. Un worker déjà enrôlé ne peut pas être déplacé par l’interface ; il faut réinitialiser son identité locale après avoir arrêté l’agent.

La découverte mDNS facilite la sélection, mais ne prouve pas l’identité du serveur. La confirmation locale est une action utilisateur, pas une attestation cryptographique auprès du serveur. Utiliser HTTPS et ne rendre l’API accessible qu’aux réseaux de confiance. L’API n’a pas encore d’authentification utilisateur pour ses routes d’administration et de lecture.

La page **Tâches** est prête pour la console worker, mais la file et le répartiteur ne sont pas encore implémentés : elle affiche explicitement cet état.

## API `v1`

- `GET /api/v1/cluster` retourne l’identité stable du cluster pour la découverte bootstrap.
- `POST /api/v1/nodes/enroll` enrôle ou retrouve le nœud à partir de son identifiant stable et de son jeton propre.
- `POST /api/v1/nodes/enrollment-requests` enregistre une demande temporaire associée à une clé de jumelage hachée.
- `POST /api/v1/nodes/enrollment-requests/approve` consomme la clé et ajoute le worker au cluster.
- `PUT /api/v1/nodes/{nodeId}/heartbeat` actualise un nœud avec son jeton dans `X-Node-Token`.
- `GET /api/v1/nodes` liste les nœuds et calcule `status` à partir de `lastSeenAt`.
- `GET /api/v1/nodes/{nodeId}` lit un nœud, ou retourne `404`.
- `GET /api/v1/libraries` liste les définitions de bibliothèques partagées.
- `POST /api/v1/libraries` crée une définition avec un nom unique et un type `video`, `image`, `music` ou `subtitles`.

Une définition de bibliothèque ne configure aucun accès média. Les chemins locaux, capacités de lecture/écriture et modes d’accès sont enregistrés séparément par nœud.
