# 0036 — Image unique et mode de nœud à chaud

Statut : implémentée (demande de Rémi : une seule image embarquant serveur, interface web et worker, avec changement du mode de fonctionnement sans redémarrer le conteneur).

## Situation actuelle

Deux programmes ASP.NET Core distincts, deux images : `Encoding.Api` (API, interface web, orchestration, port 8080, mediainfo seulement) et `Encoding.NodeAgent` (worker, API locale du worker sur 8081, FFmpeg, MKVToolNix, ImageMagick, Python et plugins). Le rôle du nœud (`Node:Roles` = `server` / `worker`) n'est lu qu'au démarrage de l'agent.

## Décision proposée

Une image `encoding` contenant les deux programmes publiés, l'interface web et les outils média. Un petit lanceur (`Encoding.Launcher`, point d'entrée de l'image) lit le mode et supervise deux processus enfants :

| Mode | Processus actifs |
| --- | --- |
| `server` | `Encoding.Api` |
| `worker` | `Encoding.NodeAgent` |
| `server,worker` | les deux |

Le lanceur démarre et arrête ces processus sans redémarrer le conteneur. Le mode courant est persisté dans le dossier de données du nœud (prioritaire sur la variable `Node__Roles`, qui donne la valeur initiale). Le changement de mode passe par l'API de gestion avec une session `admin`.

### Pourquoi des processus supervisés plutôt qu'un hôte unique

- Les deux programmes ont des conteneurs d'injection, des ports, des politiques CORS et des routes différents ; les fusionner (≈ 9 700 lignes) risque des collisions de services et d'authentification.
- Le bac à sable Landlock des plugins et des scripts reste inchangé (l'agent garde son mode `--isolated-process`).
- Arrêter le worker libère proprement ses baux : un arrêt est un cas déjà couvert par la reprise après perte d'un nœud.
- Une fusion en hôte unique reste possible plus tard sans changer l'image ni l'interface de gestion.

## Fonctionnement

- Le lanceur (`Encoding.Launcher`, point d'entrée de l'image) supervise `Encoding.Api` (port 8080) et `Encoding.NodeAgent` (port 8081). Son API de contrôle (`GET`/`PUT /mode`) écoute uniquement sur `127.0.0.1:8082`, non publié.
- **Le serveur** ne tourne que dans les modes qui l'incluent. **L'agent tourne toujours** : il porte l'identité, le signal de présence et la découverte du nœud, et son rôle `server` déclare un nœud serveur au cluster. L'agent est relancé, après un arrêt propre, uniquement quand sa fonction worker change.
- Un processus qui s'arrête sans demande est relancé avec un délai croissant (immédiat, 2 s, 5 s, 15 s, 30 s) ; un arrêt voulu n'est pas compté comme une panne. À l'arrêt du conteneur (SIGTERM), les deux processus reçoivent SIGTERM puis, après 45 s, un arrêt forcé.
- Le mode est enregistré de façon atomique dans `node-mode.json` du dossier de données du nœud (`Node__DataDirectory`). Un fichier illisible fait échouer le démarrage plutôt que d'être remplacé en silence.
- Pilotage : `GET`/`PUT /api/v1/node/mode` sur le serveur (lecture pour tout rôle, modification réservée aux administrateurs, comme toute route de gestion) et `GET`/`PUT /api/v1/worker/mode` sur le canal interne de l'agent (secret local obligatoire, voir 0044). Ces routes relaient vers le lanceur local ; elles sont définies une seule fois dans `Encoding.Core` (`NodeModeEndpoints`). Hors image unique (`Launcher:ControlUrl` absent), `GET` répond `managed: false` et `PUT` répond 409.
- L'interface propose la même boîte de dialogue « Mode du nœud » depuis la page Nœuds (administrateurs) et depuis la console du worker. Retirer la fonction serveur demande une confirmation, car l'interface cesse de répondre ; le serveur se rétablit depuis la console du worker (port 8081).
- La variable `Node__Roles` (défaut de l'image : `server,worker`) ne donne que le mode initial. L'agent rejoint par défaut le serveur local (`http://127.0.0.1:8080`) quand la fonction serveur est active et que `Cluster__ApiBaseUrl` n'est pas défini.

## Changer le mode d'un autre nœud

Demande de Rémi : depuis le serveur, un administrateur change le mode de n'importe quel nœud du cluster.

- **Mode voulu dans PostgreSQL** : migration `0031_node_desired_mode.sql`, colonne `cluster_nodes.desired_roles` (nulle sans demande). `PUT /api/v1/nodes/{id}/mode` (`admin`, fermée par défaut) enregistre la demande ; redemander le mode courant l'annule. Plusieurs serveurs voient la même demande, sans coordinateur.
- **Livraison par le signal de présence** : la réponse du heartbeat (`NodeResponse`) porte `desiredRoles` (champ additif, ignoré par les anciens agents). L'agent compare avec ses rôles déclarés et, s'ils diffèrent, appelle son lanceur local (`LauncherControlClient`) ; le lanceur persiste le mode et relance l'agent avec ses nouveaux rôles. Les rôles du heartbeat suivant égaux au mode voulu effacent la demande (comparaison faite dans la requête SQL du heartbeat). Un nœud hors ligne applique la demande à son retour. Un nœud sans lanceur ignore la demande et le journalise.
- **Observé et voulu** : l'interface (action de ligne « Changer le mode », administrateurs) affiche le mode demandé en attente dans la fiche du nœud.
- **Garde** : on ne peut pas retirer la fonction serveur du dernier nœud serveur en ligne (409) ; les changements de mode sont sérialisés par un verrou consultatif transactionnel. Un mode sans fonction est refusé (400).
- **Le lanceur relance désormais l'agent à tout changement de rôle** (serveur ou worker), afin que le heartbeat annonce les rôles réels.
- Sécurité : la demande n'arrive qu'à l'agent par sa propre connexion authentifiée par jeton de nœud ; aucune porte d'écoute n'est ajoutée sur les nœuds.

## Garanties à respecter


- **Arrêt du worker** : le lanceur envoie SIGTERM, attend 45 s puis force l'arrêt. Un bail non libéré expire (60 s) et la tâche est reprise par la reprise standard après perte d'un nœud ; aucun média n'est modifié par un arrêt forcé, la sortie reste temporaire jusqu'à validation.
- **Une seule instance de chaque programme** par conteneur ; un redémarrage du lanceur reprend le mode persisté.
- **Route fermée par défaut** (0031) : `PUT /api/v1/node/mode` exige `admin` ; le scénario d'authentification est étendu (anonyme refusé, lecteur en lecture seule). Un nœud en mode `worker` seul se pilote depuis sa console (port 8081).
- **Une seule image** : `encoding` remplace `encoding-api` et `encoding-node-agent` (décision de Rémi) ; `backend/Dockerfile.Agent` est supprimé.

## Impacts

`backend/Dockerfile` (image unique, contexte racine), `backend/docker-compose.yml` (un service `encoding`), `.github/workflows/release.yml` (publie `<utilisateur>/encoding`), `.github/workflows/backend.yml` (tests du lanceur et construction de l'image), migration 0031, contrats de nœud (`desiredRoles`, `ChangeNodeModeRequest`), `docs/operations/release.md`, `docs/operations/backup-restore.md` et `AGENTS.md`.

## Vérifications et limites

- Tests automatisés (`Encoding.Launcher.Tests`) : lecture et validation des modes, persistance, supervision (démarrage, arrêt, redémarrage de l'agent, pannes et délai de relance) et relais serveur/worker vers le lanceur. Le scénario d'authentification vérifie que la route est fermée aux anonymes, lisible par un lecteur et refusée en modification.
- Essai de bout en bout hors Docker avec les trois programmes publiés : démarrage, changement de mode par l'API du serveur et par la console du worker, relance de l'agent, relance après arrêt brutal du serveur et arrêt propre sur SIGTERM.
- Non vérifié : la construction de l'image Docker et l'exécution sous Landlock dans l'image unique (contrôlées par la CI et à confirmer sur un déploiement), et le comportement d'un cluster multi-nœuds pendant un changement de mode en charge.
- La demande à distance est livrée avec le délai du heartbeat (10 s par défaut) puis le délai du lanceur (1 s) ; non vérifié sur un cluster multi-hôtes réel.
