# 0036 — Image unique et mode de nœud à chaud

Statut : proposée (demande de Rémi : une seule image embarquant serveur, interface web et worker, avec changement du mode de fonctionnement sans redémarrer le conteneur).

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

## Garanties à respecter

- **Pas de coordinateur central** : chaque nœud change son propre mode ; aucun nœud ne force celui d'un autre.
- **Arrêt du worker** : le lanceur demande l'arrêt propre, attend la fin des tâches en cours ou la libération des baux (délai borné, 60 s de bail), puis arrête. Aucun média n'est modifié par un arrêt forcé : la sortie reste temporaire jusqu'à validation.
- **Une seule instance de chaque programme** par conteneur ; un redémarrage du lanceur reprend le mode persisté.
- **Route fermée par défaut** (0031) : `PUT /api/v1/node/mode` exige `admin` ; le test qui énumère les routes anonymes est mis à jour. Un nœud sans serveur local (mode `worker` seul) ne peut pas être piloté depuis son interface et reste piloté par les variables d'environnement ou par l'API du lanceur sur le port local.
- **Compatibilité** : les images `encoding-api` et `encoding-node-agent` restent publiables le temps de la transition.

## Impacts

Dockerfile commun (`backend/Dockerfile.All`), workflow `release.yml` (publication de `encoding` en plus ou à la place des deux images), `docker-compose.yml`, `docs/operations/release.md`, et l'AGENTS.md.

## Questions ouvertes à valider avant l'implémentation

1. Le pilotage du mode se fait-il uniquement depuis la page Nœuds du serveur, ou aussi depuis la console du worker (port 8081) quand le serveur est arrêté ?
2. Faut-il continuer à publier `encoding-api` et `encoding-node-agent`, ou les remplacer par `encoding` ?
