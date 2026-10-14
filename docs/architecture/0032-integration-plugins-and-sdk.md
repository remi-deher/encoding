# 0032 — Plugins d’intégration (Sonarr, Radarr, Plex, Jellyfin), déclencheur d’import et SDK

## Décision

Sonarr et Radarr sont les premiers plugins d’intégration exécutables. Ils couvrent deux besoins distincts, aux frontières du cluster respectées :

1. **Déclencheur (serveur)** : Sonarr/Radarr signalent un import par webhook ; le serveur Encoding transforme l’événement en exécution du flow attribué à la bibliothèque concernée. Un déclencheur doit fonctionner même si aucun worker n’est joignable à cet instant, il appartient donc au serveur et non à un processus plugin lancé à la demande par un worker.
2. **Actions (worker)** : composants de flow exécutés dans un processus plugin séparé, selon 0012 et 0015 : relire le dossier de la série/du film qui contient le fichier traité, lancer la recherche des éléments manquants, vérifier que le fichier est connu du service.

Un SDK Python et un plugin d’exemple documentent l’écriture d’un plugin tiers (`backend/plugins/README.md`).

## Plugins en Python, pas en .NET

L’isolation Landlock décrite en 0015 est obligatoire pour tout plugin. Constat : un exécutable .NET ne démarre pas dans ce bac à sable (« Failed to create CoreCLR, HRESULT 0x8007000E ») parce que CoreCLR lit `/proc/self/maps`, `/proc/meminfo`, `/proc/stat` et `/sys/devices/system/cpu`, que le bac à sable n’autorise pas. Autoriser `/proc` en lecture donnerait aussi accès à `/proc/<pid>/mem` et `/proc/<pid>/environ` de l’agent (même utilisateur), donc au jeton du nœud en mémoire. Cet élargissement n’est pas décidé ici : il est de la responsabilité du bac à sable et doit faire l’objet de sa propre décision.

Python 3 de la bibliothèque standard fonctionne dans le bac à sable (lecture des certificats, résolution DNS et réseau inclus) et est déjà installé dans l’image worker pour les scripts. Le SDK (`encoding_plugin_sdk.py`) et les plugins n’ont aucune dépendance à installer.

## Déclencheur d’import

`POST /api/v1/integrations/{sonarr|radarr}/webhook` :

- Authentification par le secret du webhook de la configuration du plugin, en-tête `X-Encoding-Webhook-Secret` ou mot de passe d’authentification de base (le nom d’utilisateur est ignoré), comparé en temps constant. Sans secret configuré, le webhook est fermé (409). Cette route s’authentifie elle-même : elle doit rester hors d’un éventuel contrôle de session utilisateur.
- `Test` répond 200 ; `Download` (import, mise à niveau comprise) est traité ; tout autre événement est ignoré (202). Les chemins viennent de `episodeFile`/`episodeFiles` ou `movieFile`/`movieFiles`, complétés par le dossier de la série/du film si le chemin complet est absent.
- La correspondance des chemins du plugin (`identifiant-bibliothèque=/chemin/vu/par/le/service`, une ligne par bibliothèque) donne la bibliothèque et le chemin relatif. `LibraryPathMapping` (`Encoding.Contracts`) est la définition unique côté C# ; le plugin Python applique le même format, et les deux implémentations sont vérifiées par les mêmes vecteurs (`backend/plugins/tests/path-mapping-vectors.json`). Un fichier hors de toute racine configurée n’est pas traité (202, compté dans `unmapped`) ; un chemin avec `..` est refusé.
- La requête n’exécute rien : elle enregistre une demande durable (`integration_trigger_requests`, une seule demande ouverte par bibliothèque et fichier, donc une livraison répétée est sans effet).
- `IntegrationTriggerService` (sur chaque serveur) réclame les demandes avec `FOR UPDATE SKIP LOCKED` et une réclamation de 60 secondes récupérable. Il demande une découverte de la bibliothèque (accès direct, comme 0016), attend une découverte terminée démarrée après la demande, retrouve le fichier dans `library_media` puis appelle le chemin commun de mise en file des bibliothèques : validation du flow attribué, réservation unique du média, reçus d’empreinte taille/date. Un chemin reçu du réseau n’est donc jamais exécuté sans avoir été découvert par un nœud ; un fichier inchangé déjà traité n’est pas retraité ; une demande dont le fichier n’apparaît pas dans l’heure expire avec un motif.

## Actions

- Le contexte d’étape (`FlowStepContext`) transporte désormais `MediaPath` et `LibraryId` (champs optionnels, compatibles avec les plugins existants). Ils sont en lecture seule et ne donnent aucun accès aux fichiers.
- Le chemin du fichier vu par le service est calculé avec la correspondance de chemins du plugin ; l’élément retenu est celui dont le dossier est le plus long préfixe (frontière de dossier respectée). Un identifiant explicite dans le composant remplace cette résolution.
- Résultat métier attendu (bibliothèque sans correspondance, aucun élément pour ce fichier) : sortie `error` ou `notFound` déclarée, aucune commande envoyée. Échec d’infrastructure (service injoignable, clé refusée, statut HTTP inattendu, redirection) : le processus se termine en erreur, jamais en sortie métier (0012, règle 5) ; l’effet est considéré incertain et la relance est explicite (0015).
- Sonarr : API `v5` puis `v3` en repli, comme le test de connexion. Radarr : `v3`. Les clés ne passent que par l’en-tête `X-Api-Key` et n’apparaissent dans aucun message.

## Plex et Jellyfin

Plex et Jellyfin utilisent le même SDK (correspondance de chemins et client HTTP JSON bornés, sans redirection) et n'ont pas de déclencheur : ils ne sont appelés que par des composants de flow.

- **Plex** (`X-Plex-Token` en en-tête) : la section est celle dont un dossier est le plus long préfixe du fichier ; l'actualisation est limitée au dossier du fichier (`/library/sections/{id}/refresh?path=`). L'attente interroge `refreshing` des sections jusqu'à la fin, bornée par l'échéance de l'invocation (marge de 5 s, 10 min au plus) ; si le scan n'est pas fini, sortie `error`, jamais un succès supposé. La vérification utilise `ratingKey` ou, à défaut, le filtre `file` de Plex, dont le résultat est revérifié sur le chemin exact de chaque partie (un filtre ignoré par un serveur ancien donne `notFound`, pas `found`).
- **Jellyfin** (`X-Emby-Token`) : le fichier traité est signalé par `POST /Library/Media/Updated` ; l'actualisation d'un élément passe par son identifiant ou par `GET /Items?Path=` (chemin exact revérifié) puis `POST /Items/{id}/Refresh` ; la vérification interroge `GET /Items?ids=` ou le chemin.
- Sans `pathMappings`, les composants gardent leur comportement global d'origine (toutes les sections Plex, `POST /Library/Refresh`) ; avec `pathMappings`, une bibliothèque sans correspondance ou un fichier hors des racines est une sortie `error` déclarée, sans appel d'actualisation.
- Limites : les API Plex et Jellyfin n'ont été vérifiées que contre des faux services ; le comportement sur de vrais serveurs (versions anciennes sans filtre `file`/`Path`) n'est pas couvert.

## Configuration

`pathMappings` et `webhookSecret` s’ajoutent au schéma de configuration de Sonarr et de Radarr, et `pathMappings` à celui de Plex et de Jellyfin (0019) ; le secret est chiffré comme les autres. Limite connue : la configuration effective du plugin est transmise au worker avec les autres valeurs, donc le secret du webhook y est inclus bien que seul le serveur en ait besoin.

## Déploiement

L’image worker embarque le SDK, le plugin et les manifestes (copies exactes du catalogue du serveur) dans `/opt/encoding/plugins/{arr,plex,jellyfin}`. Chaque worker déclare les quatre plugins dans `Runtime:Plugins` (voir `docker-compose.yml`) ; la capacité `plugin/<id>` (sonarr, radarr, plex, jellyfin) n’est annoncée que si la poignée de main réussit, et l’attribution des tâches l’exige comme pour toute capacité.

## Vérifications

Tests Python (`python3 -m unittest discover -s backend/plugins/tests`) : protocole du SDK, plugin d’exemple, plugins Sonarr/Radarr/Plex/Jellyfin comme processus réels contre un faux service (résolution par chemin, voisin au même préfixe, identifiants explicites, sorties métier, échecs d’infrastructure sans fuite de clé). Tests C# (`backend/tests/Encoding.Plugins.Tests`) : correspondance des chemins, analyse des événements, acceptation des manifestes par le catalogue, poignée de main et exécution par `PluginProcessHost` dans le bac à sable, webhook sur PostgreSQL (authentification, idempotence, découverte préalable, reçus, réclamations concurrentes, expiration). Non couverts : Sonarr/Radarr réels, plusieurs serveurs dans des processus distincts, plugin tiers installé dans un cluster multi-hôte.
