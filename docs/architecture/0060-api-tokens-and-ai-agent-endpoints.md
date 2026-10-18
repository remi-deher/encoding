# 0060 — Jetons API Cluster, cycle de vie des flows et endpoints d'automatisation IA

Statut : implémentée. Migration 0041, table `api_tokens`, synchronisation par maillage 0049.

## Besoin

Permettre à des systèmes externes, des scripts d'automatisation et des agents IA (LLM, MCP, outils de supervision) d'administrer le cluster sans dépendre d'une session de navigateur web (cookie HttpOnly SameSite=Strict et en-tête anti-CSRF).
Permettre en particulier :
- L'authentification par jeton Bearer diffusé sur l'ensemble des nœuds du cluster pour résister à la perte de n'importe quel nœud.
- La création, le clonage, la modification et la suppression de flows.
- La suppression des brouillons non publiés.
- La consultation consolidée des bibliothèques et de leur activité (statut, flow associé, exécutions en attente, en cours, terminées, échouées).

## Décision

### 1. Jetons API et résilience Cluster
- Table SQLite `api_tokens(id TEXT PRIMARY KEY, name, token_hash BLOB UNIQUE, token_prefix, role, is_active, created_at, expires_at, last_used_at, revoked_at)`.
- Le jeton en clair (`enc_pat_<64 hex>`) est généré à la création et n'est jamais stocké en clair ; seul son hachage SHA-256 (`token_hash`) et son préfixe (`token_prefix`) sont enregistrés.
- **Création du cluster** : lors de la création d'un cluster (`POST /api/v1/cluster`), un jeton cluster administrateur par défaut (`Cluster Default Token`) est automatiquement créé et renvoyé dans la réponse (`clusterToken`).
- **Diffusion maillée (0049)** : la table `api_tokens` est inscrite dans `ClusterEntityReconciliation.Tables`. Tout jeton créé ou révoqué est réconcilié sur l'ensemble des nœuds serveurs du cluster. Si le nœud créateur tombe, n'importe quel autre nœud authentifie immédiatement le même jeton.
- **Pipeline d'authentification** : `SessionAuthenticationHandler` inspecte l'en-tête `Authorization: Bearer <token>` avant le cookie de session. Si le jeton est valide et non expiré/révoqué, il attribue le rôle correspondant avec la revendication `auth_type = "api_token"`.
- **Exemption CSRF** : les requêtes M2M authentifiées par jeton Bearer sont exemptées de l'en-tête `X-Encoding-Csrf: 1` dans `ManagementAccessHandler`.

### 2. Endpoints de jetons
- `GET /api/v1/tokens` : liste des jetons (sans exposer le secret).
- `POST /api/v1/tokens` : création d'un jeton personnalisé (nom, rôle `admin` ou `viewer`, date d'expiration optionnelle).
- `DELETE /api/v1/tokens/{id}` : révocation d'un jeton.

### 3. Cycle de vie des flows
- `DELETE /api/v1/flows/{id}` : suppression d'un flow.
  - Vérifie qu'aucune bibliothèque active n'est assignée à ce flow (conflit 409 si assigné).
  - Vérifie qu'aucune exécution n'est active pour ce flow (conflit 409 si active).
  - Si le flow n'a jamais été publié (`current_version = 0` et aucune version en base), il est supprimé directement avec son brouillon.
  - Si des versions publiées existent, elles restent protégées par l'immuabilité (0004 & 0014) et le flow reçoit `deleted_at = clock_timestamp()` (suppression logique réconciliée sur le cluster).
- `DELETE /api/v1/flows/{id}/draft` : supprime le brouillon de travail sans toucher aux versions publiées.
- `POST /api/v1/flows/{id}/clone` : clone la définition courante vers un nouveau flow identifié par un nouveau GUID.
- `POST /api/v1/flows/from-template/{templateId}` : instancie un modèle de départ sous forme de nouveau flow brouillon.

### 4. Vue d'ensemble des bibliothèques
- `GET /api/v1/libraries/overview` : retourne pour chaque bibliothèque son statut d'activation, le flow assigné, le nombre de nœuds avec accès, les réglages de surveillance et les compteurs de médias et d'exécutions (en file, en cours, terminées, échouées).

### 5. Schéma OpenAPI 3.1.0
- Exposition du schéma de sécurité `bearerAuth` dans `components.securitySchemes`.
- Association de `bearerAuth` à toutes les routes non-anonymes.

## Tests

Vérifiés dans `ApiImprovementsScenarios` (`Runtime.Tests`) :
- Émission du jeton cluster lors de la création du cluster.
- Authentification Bearer sans cookie et sans en-tête CSRF.
- Création, listage, authentification et révocation de jeton personnalisé.
- Création de brouillon, clonage de flow, suppression de brouillon et suppression de flow.
- Récupération de la vue d'ensemble des bibliothèques.
- Présence de `bearerAuth` dans le schéma OpenAPI 3.1.0.
