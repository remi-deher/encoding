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

Sans configuration de base de données, l'image utilise SQLite dans le dossier de données (`/var/lib/encoding-node/cluster.db`). Chaque serveur garde son propre fichier ; les données se réconcilient par le maillage opportuniste 0049 :

```
docker run -d --name encoding -p 8080:8080 \
  -e Node__Roles=server,worker \
  -v encoding-node:/var/lib/encoding-node \
  <utilisateur>/encoding:latest
```

SQLite est le seul fournisseur de données. Le fichier est `/var/lib/encoding-node/cluster.db` et reste propre à chaque nœud. La clé de données du cluster est générée lors de sa création et transmise chiffrée à chaque serveur approuvé ; aucune variable de secret de réconciliation n’est à configurer. Les identités privées des serveurs et la clé locale Data Protection doivent rester dans le volume `PluginSettings:KeyDirectory` (`/var/lib/encoding/plugin-keys` dans Compose). Les pairs mDNS du même cluster sont découverts automatiquement. La découverte mDNS ne traverse pas toujours les réseaux Docker/VLAN : ajoutez alors des adresses joignables avec `Cluster__Reconciliation__Peers__0`, `__1`, etc., et ouvrez le port API entre les serveurs. `Cluster__Reconciliation__AdvertisedApiBaseUrl` sert à annoncer l'adresse de pair joignable ; il est distinct de `Cluster__AdvertisedApiBaseUrl`, qui peut être une adresse interne réservée aux workers locaux. Dans Docker Compose, renseignez au besoin `CLUSTER_RECONCILIATION_ADVERTISED_API_BASE_URL` et `CLUSTER_RECONCILIATION_PEER_0`. Pour utiliser l'exemple Compose entre plusieurs hôtes, définissez aussi `CLUSTER_API_BIND_ADDRESS` sur l'adresse réseau à laquelle le port 5080 doit être publié ; il reste lié à la boucle locale par défaut. La page Nœuds → Cluster affiche l'état de découverte et les erreurs de synchronisation ; `GET /api/v1/cluster/reconciliation/status` expose le même diagnostic aux utilisateurs authentifiés. Cette version attend une base de données SQLite fraîche : réinitialisez les bases existantes avant le redémarrage, comme prévu pour cette installation.

`Node__Roles` (`server`, `worker` ou `server,worker`) donne le mode initial. Ensuite le mode se change depuis la page Nœuds (« Ce nœud et son cluster » → « Mode du nœud »), sans redémarrer le conteneur ; le choix est conservé dans le dossier de données du nœud. Un worker seul doit définir `Cluster__ApiBaseUrl` (ou être découvert sur le réseau) ; le volume de données doit être conservé pour garder l'identité du nœud et son mode.

## Gérer ce nœud depuis la page Nœuds

Sur un nœud qui héberge le serveur, tout se gère depuis la page **Nœuds → Ce nœud et son cluster** : mode, adresse d'un cluster (`http://<serveur>:8080`, utile quand la découverte DNS-SD ne traverse pas le réseau), clé de jumelage, rejoindre, quitter. Le serveur relaie ces actions vers l'agent du même conteneur (`LocalAgent__BaseUrl`, par défaut `http://127.0.0.1:8081/`) derrière la session administrateur et avec le secret local créé par le lanceur. L'agent écoute uniquement en boucle locale, exige le secret pour toutes ses routes de gestion et n'utilise plus `Origin` ni CORS : retirer les anciens ports 8081 publiés et le réglage `WorkerUi__Origins` lors de la mise à jour. La gestion par navigateur d'un nœud nécessite la fonction serveur locale ; un nœud worker seul se pilote depuis son cluster, ou via le canal interne authentifié. Hors image unique, le serveur et l'agent du même hôte doivent partager explicitement le fichier `Node__LocalEnrollmentSecretFile`.
