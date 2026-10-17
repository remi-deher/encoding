# 0031 — Authentification, rôles et durcissement du jumelage

- Statut : accepté
- Date : 14 octobre 2026

## Contexte

L’API de gestion était ouverte : toute personne joignant le serveur pouvait lire et modifier flows, bibliothèques et nœuds, approuver un worker ou même enrôler un nœud sans validation. [0002](0002-worker-discovery-and-enrollment.md) renvoyait cette protection au réseau.

## Décision

**Utilisateurs et rôles.** Les comptes vivent dans SQLite local et sont réconciliés entre serveurs par le maillage 0049. Deux rôles : `admin` (tout) et `viewer` (lecture seule). Le premier administrateur est créé par `POST /api/v1/auth/setup`, possible uniquement tant qu’aucun compte n’existe sur ce nœud ; les modifications concurrentes de comptes sur des nœuds isolés convergent selon la règle LWW. `Auth__SetupToken`, s’il est défini, est exigé par cette route. Le dernier administrateur actif ne peut être ni rétrogradé, ni désactivé, ni supprimé, et un administrateur ne peut pas retirer ses propres droits.

**Sessions.** Un cookie opaque (`encoding_session`, HttpOnly, SameSite=Strict, Secure selon `Auth__CookieSecure` ou HTTPS détecté) contient 256 bits aléatoires ; seul son SHA-256 est stocké dans la table locale `user_sessions`. Les sessions ne sont pas réconciliées : un utilisateur se connecte sur chaque serveur qu’il utilise. Expiration par inactivité (`Auth__SessionIdleMinutes`, 480) et durée absolue (`Auth__SessionAbsoluteHours`, 168). Le rôle et l’état du compte sont relus à chaque requête : désactiver un compte ou changer un rôle prend effet après convergence, et un changement de mot de passe ferme les sessions locales de l’utilisateur.

**Mots de passe.** PBKDF2-HMAC-SHA512, 210 000 itérations, sel aléatoire, format auto-descriptif permettant d’augmenter le coût plus tard (re-hachage à la connexion suivante). Longueur 12 à 256. Après 5 échecs consécutifs (`Auth__MaxFailedAttempts`), le compte est verrouillé 1 minute puis jusqu’à 15 minutes en doublant. Les échecs (compte inconnu, mauvais mot de passe, compte inactif ou verrouillé) sont indiscernables par la réponse et par le temps de calcul. Les routes anonymes sont limitées par adresse source (`Auth__AnonymousRequestsPerMinute`, 20) ; derrière un proxy, activer `Auth__TrustForwardedHeaders` et ne laisser joindre l’API que par lui.

**Autorisation unique.** Une politique de repli s’applique à toute route, y compris celles ajoutées plus tard : session obligatoire ; GET/HEAD pour tout rôle ; toute autre méthode réservée aux administrateurs, avec l’en-tête `X-Encoding-Csrf: 1` (une requête inter-sites ne peut pas l’ajouter sans préambule CORS, qui n’est jamais accordé). Exceptions explicites :

- `.AllowViewer()` : POST sans effet sur l’état du cluster (validation et simulation de flow, import MediaInfo temporaire, export d’archive), plus la déconnexion et le changement de son propre mot de passe.
- `.AllowAnonymous()` : santé, identité du cluster (découverte), `auth/status|setup|login`, le protocole des nœuds (`enroll`, `enrollment-requests` POST, heartbeat, `runtime/nodes/{id}/**`) et le fichier de l’interface.
- `.AllowNodeCredentials()` : `GET /api/v1/executions/{id}` accepte aussi un `X-Node-Token` valide, car le worker lit l’état d’une exécution. Aucun autre accès de gestion n’est ouvert à un jeton de nœud.
- `/api/v1/users/**` : administrateur, même en lecture.

Un test énumère les routes anonymes et échoue si la liste change sans décision explicite.

**Jumelage des nœuds.**

- L’approbation d’une demande (par clé ou par identifiant) exige un administrateur.
- `POST /nodes/enroll` (confirmation locale du worker) n’accepte plus une nouvelle identité par défaut. Un administrateur ouvre une **fenêtre d’enrôlement direct** de 1 à 60 minutes (`PUT /nodes/enrollment-window`, fermée par `DELETE`), stockée dans SQLite local et réconciliée avec les pairs. Une identité déjà connue avec le même secret peut répéter son appel hors fenêtre ; un secret différent est refusé dans tous les cas. `Worker__AutoAcceptEnrollment` suppose donc une fenêtre ouverte.
- Les demandes de jumelage en attente sont plafonnées (`Auth__MaxPendingPairingRequests`, 20) ; au-delà, `429`. Un worker déjà en attente peut rafraîchir sa propre demande.
- `enroll` et `enrollment-requests` partagent la limite par adresse des routes anonymes.

## Compatibilité et migration

- Migration `0026_users_and_sessions.sql` : `users`, `user_sessions`, `enrollment_window`.
- Les agents existants continuent de fonctionner : heartbeat et routes `runtime` n’ont pas changé de contrat, et la lecture d’une exécution accepte leur jeton. Un agent plus ancien reçoit `403` à l’enrôlement direct tant qu’aucune fenêtre n’est ouverte.
- Après mise à jour, tout accès à l’interface demande d’abord la création du premier administrateur : à faire avant d’exposer le serveur.
- Le cookie n’est pas partagé entre origines : l’interface Angular et l’API restent servies par le même hôte (ou le proxy de développement).

## Limites

- Pas encore de SSO/OIDC ni d’authentification à deux facteurs ; le contrat d’identité (`ClaimsPrincipal` avec un rôle) permet d’ajouter un fournisseur plus tard.
- Pas de jetons d’API pour des scripts ; les intégrations passent par un compte.
- Le rôle lecture est appliqué par le serveur. L’interface masque Paramètres et l’enrôlement direct, mais les autres boutons d’écriture restent affichés et répondent par un message d’accès refusé.
- Le verrouillage de compte permet à un tiers connaissant un nom d’utilisateur de bloquer ce compte temporairement ; un administrateur peut le débloquer en réinitialisant le mot de passe.
- Le trafic reste en clair sans HTTPS : terminer TLS devant l’API et définir `Auth__CookieSecure=true`.
