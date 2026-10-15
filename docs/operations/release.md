# Publication des images Docker

Le workflow `.github/workflows/release.yml` s'exécute à chaque push sur `main` touchant `backend/` ou `frontend/`. Il attend que les contrôles CI du commit (Backend, Frontend) soient tous terminés et verts, puis publie sur Docker Hub :

- `<utilisateur>/encoding` (`backend/Dockerfile`, contexte racine) : image unique contenant le serveur, l'interface web, le worker et le lanceur qui les démarre selon le mode du nœud (voir l'architecture 0036).

L'image reçoit les tags `X.Y.Z` et `latest`, et le commit reçoit le tag git `vX.Y.Z`.

## Secrets à créer

Dans GitHub : Settings > Secrets and variables > Actions > New repository secret.

- `DOCKERHUB_USERNAME` : nom d'utilisateur Docker Hub ;
- `DOCKERHUB_TOKEN` : jeton d'accès Docker Hub (Account settings > Personal access tokens, droits Read & Write).

## Numéro de version

Calculé par `.github/scripts/next-version.sh` à partir du dernier tag `vX.Y.Z` (départ 0.0.0) :

- patch par défaut ;
- minor : libellé de PR `release:minor` ou commit `feat:` / `minor:` ;
- major : libellé de PR `release:major`, commit `type!:` ou `major:`.

## Utiliser l'image

Sans configuration de base de données, l'image utilise SQLite dans le dossier de données (`/var/lib/encoding-node/cluster.db`), ce qui convient à un cluster à un seul serveur (architecture 0043) :

```
docker run -d --name encoding -p 8080:8080 -p 8081:8081 \
  -e Node__Roles=server,worker \
  -v encoding-node:/var/lib/encoding-node \
  <utilisateur>/encoding:latest
```

Pour PostgreSQL (obligatoire dès qu'un cluster a plusieurs serveurs), fournir la chaîne de connexion : le fournisseur est alors déduit de la chaîne (`Host=…`), ou imposé par `Database__Provider=postgres|sqlite`.

```
  -e ConnectionStrings__Cluster="Host=...;Database=encoding;Username=...;Password=..."
```

Pour passer d'un mode à l'autre, voir `Encoding.Api transfer-database` dans [sauvegarde et restauration](backup-restore.md).

`Node__Roles` (`server`, `worker` ou `server,worker`) donne le mode initial. Ensuite le mode se change depuis la page Nœuds (« Ce nœud et son cluster » → « Mode du nœud »), sans redémarrer le conteneur ; le choix est conservé dans le dossier de données du nœud. Un worker seul doit définir `Cluster__ApiBaseUrl` (ou être découvert sur le réseau) ; le volume de données doit être conservé pour garder l'identité du nœud et son mode.

## Gérer ce nœud depuis la page Nœuds

Sur un nœud qui héberge le serveur, tout se gère depuis la page **Nœuds → Ce nœud et son cluster** : mode, adresse d'un cluster (`http://<serveur>:8080`, utile quand la découverte DNS-SD ne traverse pas le réseau), clé de jumelage, rejoindre, quitter. Le serveur relaie ces actions vers l'agent du même conteneur (`LocalAgent__BaseUrl`, par défaut `http://127.0.0.1:8081/`) derrière la session administrateur : il n'y a rien à publier ni de CORS à régler. Seul un nœud sans serveur (worker seul) garde la page minimale `/worker`, qui parle directement à l'agent (`localhost:5081`, ou même hôte sur le port 8081 ; `WorkerUi__Origins` autorise l'origine de l'interface et `localStorage['encoding.workerAgentUrl']` redéfinit l'URL).
