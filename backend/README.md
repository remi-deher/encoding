# Backend

Socle API ASP.NET Core sur .NET 10 et PostgreSQL partagé. L’API et l’agent échangent les contrats versionnés du projet `Encoding.Contracts`. Les décisions d’architecture sont décrites dans [`../docs/architecture/`](../docs/architecture/).

## Développement local

Prérequis : Docker Compose. Créer le fichier d’environnement local puis démarrer les services :

```powershell
Copy-Item .env.example .env
docker compose up --build
```

L’interface Angular et l’API sont servies par le même conteneur : ouvrir `http://localhost:4201` pour l’application ou `http://localhost:5080` pour l’API. La console locale de l’agent reste disponible sur `http://localhost:5081` et PostgreSQL sur `localhost:5432`. Depuis le poste worker, ouvrir l’interface Angular à `/worker`. Le navigateur appelle `localhost:5081` sur le poste où il s’exécute ; les origines autorisées se règlent avec `WORKER_UI_ORIGINS`. Les données d’identité du worker sont persistées dans le volume `encoding-node-data`.

Les migrations SQL versionnées (`NNNN_nom.sql`, numéro unique, empreinte enregistrée) s’appliquent au démarrage, sous verrou PostgreSQL ; ne jamais modifier une migration publiée, en ajouter une. Pour ne lancer que la base : `docker compose up -d postgres`. Les identifiants PostgreSQL Compose sont réservés au développement local.

Le worker découvre les clusters annoncés en DNS-SD/mDNS (`_encoding._tcp`). Le service prend aussi en charge une adresse bootstrap `Cluster:ApiBaseUrl`, utilisée ici pour la découverte dans le réseau Compose où le multicast peut être indisponible. Pour un worker sur un autre hôte, définissez `CLUSTER_ADVERTISED_API_BASE_URL` avec une URL HTTPS joignable depuis ces machines, puis publiez le port API sur l’interface réseau prévue et configurez les origines navigateur. Le réglage de développement lie volontairement l’API à la boucle locale.

## Enrôlement worker

1. Démarrer l’agent sur la machine worker.
2. Ouvrir `/worker` depuis le navigateur de cette même machine.
3. Dans l’onglet **Nœud**, sélectionner le cluster détecté puis confirmer **Rejoindre**.

Ce parcours (confirmation locale) n’est accepté que si un administrateur a ouvert la **fenêtre d’enrôlement direct** depuis **Nœuds → Ajouter un nœud** (10 minutes). Autre parcours, sans fenêtre : cliquer sur **Générer une clé** dans **Nœuds → Ce nœud et son cluster** du nœud à ajouter, puis ouvrir **Ajouter un nœud** dans le catalogue du serveur et saisir la clé ; l’approbation exige un administrateur. Elle expire après 10 minutes et ne peut servir qu’une fois. Le worker attend la confirmation sur son heartbeat avant de se déclarer enrôlé.

L’acceptation automatique se règle sur le worker avec `Worker__AutoAcceptEnrollment` (Compose : `WORKER_AUTO_ACCEPT_ENROLLMENT`). Elle est désactivée par défaut. Lorsqu’elle est activée, le worker accepte automatiquement le seul cluster détecté ; s’il en voit plusieurs, il attend un choix explicite.

L’identité et le jeton aléatoire sont créés par l’agent, puis conservés dans son répertoire de données. Le serveur stocke uniquement le SHA-256 du jeton et l’exige pour les heartbeats via `X-Node-Token`. Les répétitions d’un enrôlement avec la même identité et le même jeton sont idempotentes. Un worker déjà enrôlé ne peut pas être déplacé par l’interface ; il faut réinitialiser son identité locale après avoir arrêté l’agent.

La découverte mDNS facilite la sélection, mais ne prouve pas l’identité du serveur. La confirmation locale est une action utilisateur, pas une attestation cryptographique auprès du serveur. Utiliser HTTPS et ne rendre l’API accessible qu’aux réseaux de confiance.

La page **Tâches** est prête, mais la file et le répartiteur ne sont pas encore implémentés : elle affiche explicitement cet état.

## Authentification

Au premier démarrage, l’interface demande de créer le premier administrateur (`/setup`). Définir `Auth__SetupToken` (Compose : `AUTH_SETUP_TOKEN`) pour exiger un jeton lors de cette création si le serveur est joignable avant que vous l’ayez faite. Les autres comptes se gèrent dans **Paramètres** ; le rôle lecture seule consulte sans modifier. Réglages optionnels, identiques pour tous les serveurs du cluster : `Auth__SessionIdleMinutes` (480), `Auth__SessionAbsoluteHours` (168), `Auth__MaxFailedAttempts` (5), `Auth__MaxPendingPairingRequests` (20), `Auth__AnonymousRequestsPerMinute` (20), `Auth__CookieSecure` (`auto`, `true` ou `false` ; `true` derrière un proxy TLS) et `Auth__TrustForwardedHeaders` (`true` derrière un proxy de confiance, pour limiter par adresse réelle). Voir [0031](../docs/architecture/0031-authentication-roles-and-pairing.md).

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

## Suivi des exécutions

La migration 0008 ajoute le registre et son journal. L’API /api/v1/executions expose les compteurs et tentatives persistants, les demandes d’annulation et les relances. Le runtime utilise ce registre, contrôle les baux et valide les sorties avant une réussite. Voir docs/architecture/0009-execution-tracking.md pour les contrats et les limites.

## Préparation du moteur de flow

Les contrats de préparation se trouvent dans Encoding.Contracts/Flows et Encoding.Contracts/Plugins : définition versionnée, manifeste déclaratif, invocation et résultat d’étape. Ils sont exposés par l’API et exécutés par l’agent via le runtime décrit dans l’architecture 0015. L’ordre d’implémentation et les garanties attendues sont décrits dans [l’architecture 0012](../docs/architecture/0012-flow-engine-foundation.md).

## Catalogue et validation des flows

GET /api/v1/flows/components expose les manifestes installés (Base 1.0.0, 44 composants). POST /api/v1/flows/validate reçoit le document FlowDefinition et retourne isValid et issues ; une définition invalide reçoit HTTP 422. Les erreurs comprennent code, path (JSON Pointer) et message. POST /api/v1/executions utilise la même validation et contrôle la concordance de flowVersion. Il n’accepte plus un objet JSON arbitraire comme définition.

Le manifeste déclaratif Base est dans src/Encoding.Api/Flows/Catalog/base.json. Le registre le charge au démarrage ; une absence ou incohérence bloque le démarrage. La présence d’un descripteur ne signifie pas qu’un handler d’exécution est disponible. Les manifestes tiers configurés sont chargés comme descripteurs ; leur exécution reste dans un processus séparé sur le worker.

## Bibliothèque de scripts

La migration 0018 ajoute les brouillons et versions publiées de scripts partagés. L’API `GET/POST /api/v1/scripts`, `GET /api/v1/scripts/{id}`, `PUT /api/v1/scripts/{id}/draft`, `POST /api/v1/scripts/{id}/publish` et `GET /api/v1/scripts/{id}/versions/{version}` gère ces ressources avec contrôle de concurrence. Un flow garde une copie du code choisi et une référence de provenance vers la version publiée ; voir [0024](../docs/architecture/0024-script-library.md).

## Modèles de flow

La migration 0019 ajoute les modèles partagés. `POST /api/v1/templates/from-flow` copie une version publiée précise, et `GET /api/v1/templates` ainsi que `GET /api/v1/templates/{id}` les exposent à l’interface. L’éditeur charge le modèle sous un nouvel identifiant de flow ; voir [0025](../docs/architecture/0025-flow-templates.md).

## Versions et sauvegardes de flows

La migration 0009 ajoute les versions immuables et la file de sauvegardes. GET /api/v1/flows liste les flows ; GET /api/v1/flows/{id} et /versions/{version} lisent leurs documents ; PUT /api/v1/flows/{id} publie une version validée avec expectedVersion. Un conflit concurrent reçoit HTTP 409. La création d’exécution exige une version enregistrée.

FlowBackups__Directory configure le dossier JSON. Compose monte encoding-flow-backups dans /var/lib/encoding/flow-backups, séparément de PostgreSQL. Les erreurs sont visibles dans backupStatus et retentées. En cluster multi-hôte, monter un dossier commun durable sur tous les serveurs consommateurs. Voir [0014](../docs/architecture/0014-flow-version-storage-and-backups.md) pour la récupération et les limites.


## Runtime, brouillons et restauration

Voir [0015 — Runtime et archives portables](../docs/architecture/0015-flow-runtime-and-portable-archives.md) pour les baux, checkpoints, outils, contrats plugin et restrictions Linux Landlock.

- Le tableau propose brouillons, publication validée, comparaison des versions, import JSON/ZIP, export JSON individuel et export ZIP d’une sélection.
- La bibliothèque reçoit une version publiée du flow. Découvrez les fichiers puis traitez une sélection depuis la bibliothèque ; aucun chemin n’est saisi. Configurez les montages et les accès des nœuds au préalable. Voir [0016](../docs/architecture/0016-library-workflow-and-discovery.md).
- Scripts et plugins nécessitent Landlock sur le noyau Linux hôte ; ils ne sont pas annoncés si cette restriction est absente.
- Le nœud source transfère le média par WebDAV ; les outils travaillent sur une copie locale. Le résultat validé est publié sous .encoding-results/<executionId>/ sans remplacer la source.
- Les métadonnées avant/après, événements et fichiers produits sont consultables dans le suivi.
- Runtime__ArtifactDirectory désigne le stockage durable des blobs. Compose ajoute un volume séparé ; les serveurs multi-hôtes doivent utiliser un stockage partagé. Prévoir sa capacité : aucun nettoyage automatique n’est activé.

## Testeur de flow

Depuis l’éditeur, **Tester le flow** explore le document en cours avec un profil fictif ou un fichier importé via MediaInfo. Le nom, les métadonnées, les pistes et les variables restent modifiables. Les conditions natives affichent leurs comparaisons et sorties ; les actions externes s’arrêtent pour demander le résultat à supposer avant de poursuivre.

Aucune exécution, conversion, invocation de plugin ou transfert réel n’est créé. Les résultats du test restent dans la session de l’éditeur. L’import MediaInfo est limité à 512 Mio, deux minutes et deux lectures simultanées par serveur ; la copie temporaire est supprimée après lecture. Voir [0023](../docs/architecture/0023-flow-simulation.md) pour les contrats et limites.

L’action Enregistrer de l’éditeur valide automatiquement le document : publication s’il est valide, brouillon sinon. Une publication inchangée retourne la version existante et ne crée ni version ni tâche de sauvegarde supplémentaire. Ce contrôle transactionnel conserve les conflits de concurrence et les versions des archives importées.

## Exploitation

`/health/live`, `/health/ready` et `/health/details` décrivent l’état du serveur, `/metrics` publie les métriques Prometheus, et les images journalisent en JSON avec un identifiant `X-Correlation-Id`. La rétention des artefacts est désactivée par défaut. Voir [supervision](../docs/operations/monitoring.md), [sauvegarde et restauration](../docs/operations/backup-restore.md) et l’architecture 0029.
