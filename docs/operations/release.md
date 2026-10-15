# Publication des images Docker

Le workflow `.github/workflows/release.yml` s'exécute à chaque push sur `main` touchant `backend/` ou `frontend/`. Il attend que les contrôles CI du commit (Backend, Frontend) soient tous terminés et verts, puis publie sur Docker Hub :

- `<utilisateur>/encoding-api` (`backend/Dockerfile`, contexte racine) ;
- `<utilisateur>/encoding-node-agent` (`backend/Dockerfile.Agent`, contexte `backend`).

Chaque image reçoit les tags `X.Y.Z` et `latest`, et le commit reçoit le tag git `vX.Y.Z`.

## Secrets à créer

Dans GitHub : Settings > Secrets and variables > Actions > New repository secret.

- `DOCKERHUB_USERNAME` : nom d'utilisateur Docker Hub ;
- `DOCKERHUB_TOKEN` : jeton d'accès Docker Hub (Account settings > Personal access tokens, droits Read & Write).

## Numéro de version

Calculé par `.github/scripts/next-version.sh` à partir du dernier tag `vX.Y.Z` (départ 0.0.0) :

- patch par défaut ;
- minor : libellé de PR `release:minor` ou commit `feat:` / `minor:` ;
- major : libellé de PR `release:major`, commit `type!:` ou `major:`.
