# Backend

Socle API ASP.NET Core sur .NET 10 et PostgreSQL partagé. L'API et l'agent échangent les contrats versionnés du projet `Encoding.Contracts`. La décision d'architecture est décrite dans [`../docs/architecture/0001-shared-state-and-node-presence.md`](../docs/architecture/0001-shared-state-and-node-presence.md).

## Développement local

Prérequis : Docker Compose. Avant le premier démarrage, créer `backend/.env` à partir de `.env.example`, puis remplacer `NODE_API_TOKEN` par un secret aléatoire et `NODE_ID` par un UUID unique. `.env` est ignoré par Git.

```powershell
Copy-Item .env.example .env
# Remplacer NODE_API_TOKEN et NODE_ID dans .env avant le démarrage.
docker compose up --build
```

L'API démarre sur `http://localhost:5080`, l'agent de nœud démarre dans un conteneur séparé et PostgreSQL reste lié à la boucle locale sur le port `5432`. Les migrations SQL versionnées sont appliquées dans une transaction au démarrage, sous verrou PostgreSQL pour éviter les courses entre instances qui démarrent ensemble. Pour lancer uniquement PostgreSQL : `docker compose up -d postgres`.

La connexion se règle avec `ConnectionStrings__Cluster`. Le seuil de présence se règle avec `NodePresence__OfflineAfterSeconds` (30 secondes par défaut). `GET /health/live` vérifie le processus ; `GET /health/ready` vérifie PostgreSQL.

## API `v1`

- `PUT /api/v1/nodes/{nodeId}/heartbeat` enregistre ou actualise un nœud (upsert idempotent par identifiant stable).
- `GET /api/v1/nodes` liste les nœuds et calcule `status` à partir de `lastSeenAt`.
- `GET /api/v1/nodes/{nodeId}` lit un nœud, ou retourne `404`.
- `GET /api/v1/libraries` liste les définitions de bibliothèques partagées.
- `POST /api/v1/libraries` crée une définition avec un nom unique et un type `video`, `image`, `music` ou `subtitles`.
- Le heartbeat exige le jeton partagé dans l'en-tête `X-Node-Token`.

La définition d’une bibliothèque ne configure aucun accès média. Les chemins locaux, capacités de lecture/écriture et modes d’accès seront enregistrés séparément par nœud.

Exemple de signal :

```json
{
  "name": "worker-gpu-01",
  "roles": ["worker"],
  "maxConcurrentTasks": 3,
  "capabilities": [
    {
      "toolId": "ffmpeg",
      "version": "7.1",
      "mediaTypes": ["video", "audio"],
      "accelerationTypes": ["nvenc"]
    }
  ]
}
```

Le statut est calculé par PostgreSQL à partir du dernier signal et du seuil configuré. L'agent conserve l'identifiant configuré, envoie un signal toutes les 10 secondes et détecte FFmpeg lorsqu'il est installé dans son environnement. Les capacités restent des déclarations observées : elles ne donnent pas accès aux bibliothèques et ne rendent pas un worker éligible à une tâche à elles seules.

## Limite de sécurité actuelle

Le signal des nœuds est protégé par un jeton partagé configuré via `NODE_API_TOKEN`. L'enrôlement, la rotation des jetons et l'authentification utilisateur des routes de lecture restent à définir. Compose lie l'API et PostgreSQL à la boucle locale pour le développement ; ne pas exposer l'API à un réseau non fiable. Utiliser HTTPS sur tout réseau de cluster hors de cette configuration locale. Les identifiants de connexion Compose sont uniquement destinés au développement local.
