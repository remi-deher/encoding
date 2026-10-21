# Exploiter un cluster pair à pair

Le mode `PeerCluster` est optionnel. Trois serveurs conservent chacun une base SQLite locale et un journal Raft indépendants ; la majorité confirme les changements métier. Tous les pairs utilisent la même version applicative. Aucun serveur central permanent n’est requis. Voir [0061](../architecture/domains/replication-storage.md).

## Ce qui est partagé

Toutes les tables métier sont répliquées : bibliothèques, nœuds autorisés à les traiter, accès directs/transferts, chemins par nœud, profils, modèles, scripts, packs, flows et brouillons, configurations de plugins chiffrées, workers et leurs jetons, tâches, baux, réservations, checkpoints et résultats. Les imports de flows restent atomiques.

Les comptes administrateurs et leurs sessions restent propres à chaque API ; un répartiteur HTTP doit garder une affinité de session pour les utilisateurs connectés. Les lectures viennent de la projection locale et peuvent être anciennes pendant une partition ; une mutation reçue sur un follower est transmise au leader. Sans quorum, les mutations partagées et les nouvelles attributions sont refusées. Après une élection, les appels workers portant un ancien terme sont refusés.

La présence est diffusée par gossip signé ; les relais ne prolongent pas artificiellement un signal ancien et ne réintroduisent pas les nœuds retirés du catalogue engagé. Les vues convergent après reconnexion. Un effet externe déjà lancé par un plugin ne peut pas être annulé par le consensus : une invocation non idempotente interrompue échoue explicitement et exige une décision de relance.

## Préparer les trois serveurs

Chaque serveur doit avoir une base et un volume pair persistants exclusifs. Utiliser un GUID de cluster commun et une URL HTTPS racine stable par pair. Le certificat local inclut l’hôte de cette URL.

```text
PeerCluster__Enabled=true
PeerCluster__ClusterId=<GUID commun>
PeerCluster__Bootstrap=false
PeerCluster__PublicEndpoint=https://<hôte du pair>:8443/
PeerCluster__ListenPort=8443
PeerCluster__IdentityPassword=<secret propre au pair, au moins 32 caractères>
PeerCluster__SharedDataKey=<même clé base64 de 32 octets sur les trois pairs>
PeerCluster__DataDirectory=/var/lib/encoding-node/peer-cluster
PluginSettings__KeyDirectory=/var/lib/encoding-node/plugin-keys
```

Générer la clé commune avec `openssl rand -base64 32` et chaque mot de passe d’identité avec `openssl rand -base64 48`. Les conserver dans le gestionnaire de secrets. La perte de l’un de ces secrets empêche la récupération des données chiffrées correspondantes. Le mot de passe d’identité protège la clé privée du pair ; la clé commune protège les secrets applicatifs répliqués.

Sur le premier serveur uniquement, utiliser `Bootstrap=true` pour engager l’image de sa base faisant autorité. Après cet import, remettre la configuration à `false` pour les redémarrages. Les nouveaux pairs utilisent toujours `false`. Ne jamais réinitialiser le dossier Raft d’un pair existant.

Compose expose les variables `PEER_CLUSTER_*`, dont `PEER_CLUSTER_SHARED_DATA_KEY`. Le port pair 8443 est lié à la boucle locale par défaut ; définir `PEER_CLUSTER_BIND` pour les pairs distants et limiter ce port aux serveurs autorisés. Il exige TLS mutuel, une approbation explicite et un protocole/schéma compatibles. Il transporte Raft, le gossip, le statut interne et les requêtes API relayées. Le port API habituel continue de servir les utilisateurs et workers.

## Accès aux bibliothèques et aux fichiers

Configurer les nœuds autorisés à traiter chaque bibliothèque, puis leur accès :

- **Direct** : disque, NFS, USB ou autre montage visible localement ; chemin propre au nœud, droits de lecture/écriture explicites.
- **Transfert** : le worker n’héberge pas la bibliothèque. Une source déclarée avec accès direct lui fournit le média par le WebDAV inter-nœuds existant ; il traite sa copie temporaire et renvoie le résultat.

Les fichiers ne sont pas répliqués par Raft. Si aucune source directe configurée n’est disponible, la tâche échoue à l’attribution ou à la prochaine passe de reprise (une seconde), avec libération de sa réservation. Un transfert échoué fait aussi échouer la tâche. Elle n’attend pas le retour de la source.

Préserver les volumes d’artefacts et de sauvegardes de flows déjà utilisés par l’installation. Les serveurs qui servent ces fichiers doivent y avoir accès, par exemple par des montages communs. Leurs sauvegardes et leur redondance relèvent du stockage externe ; une base répliquée n’en fournit pas une copie.

## Approuver et ajouter un pair

1. Démarrer le premier serveur et attendre une projection prête et un leader avec quorum. Initialiser le compte administrateur local sur chaque nouvelle API.
2. Lire `GET /api/v1/peer-cluster/identity` sur chaque API normale. Comparer `clusterId`, `peerId`, `endpoint`, `certificatePem` et `thumbprint` par un canal d’administration fiable.
3. Approuver chaque autre pair avec `POST /api/v1/peer-cluster/peers` : `peerId`, `clusterId`, `endpoint`, `certificatePem`, `fingerprint` (la valeur de `thumbprint`). Répéter dans les deux sens entre toutes les paires. Une découverte ou un GUID commun n’accorde pas la confiance.
4. Pour un nouveau pair vide, aucune copie SQL manuelle n’est nécessaire : l’image engagée et le WAL initialisent ses tables partagées. Depuis le leader, appeler `POST /api/v1/peer-cluster/peers/{peerId}/membership`, un pair à la fois.
5. Comparer `GET /api/v1/peer-cluster/consensus` : même membership, leader unique, `hasQuorum=true` sur le leader et index engagés convergents. Vérifier `/health/ready`, les révisions de projection dans `/metrics`, puis `/api/v1/nodes` et la vue Cluster.

Les routes de gestion exigent une session administrateur et les en-têtes CSRF habituels. La confirmation d’un changement de membership ne suffit pas à déclarer le nouveau pair à jour : attendre son rattrapage. Une incompatibilité de protocole, schéma, fournisseur ou clé refuse le rattachement ; deux groupes initialisés indépendamment ne sont jamais fusionnés.

Pour retirer un pair, appeler `DELETE /api/v1/peer-cluster/peers/{peerId}` sur le leader. Retrait et révocation demandent le quorum. L’auto-révocation et le retrait du dernier membre sont refusés. Pour réintégrer un pair révoqué, réapprouver explicitement son certificat avant sa réactivation et son ajout. Conserver un nombre impair de votes ; deux membres ne tolèrent aucune perte.

## Initialiser depuis une installation existante

Désigner une seule base faisant autorité ; arrêter les écritures et les agents pendant la migration. La base d’origine reste intacte. Copier vers une cible vide, avec le binaire correspondant au schéma source :

```sh
dotnet Encoding.Api.dll initialize-peer-database \
  --authority "$AUTHORITY_CONNECTION" \
  --target "$NEW_PEER_CONNECTION"
```

La commande vérifie la version source, migre la cible, copie les tables SQLite dans une transaction cohérente et vérifie les compteurs. Une cible peuplée est refusée.

Copier les clés locales de protection de l’ancienne autorité pour que le premier pair puisse déchiffrer puis rechiffrer les configurations historiques avec `SharedDataKey`. Démarrer ce premier pair en bootstrap et ajouter les pairs vides ensuite. Ne jamais importer une autre base indépendante par-dessus cette autorité. L’image initiale et chaque transaction sont limitées à 64 Mio ; un import supérieur demande une évolution préalable du protocole, sans tronquer les données.

## Sauvegarde à froid et restauration

Une sauvegarde de pair contient sa base convertie en SQLite, son identité privée chiffrée, sa membership, son WAL, ses instantanés et ses clés Data Protection. Un manifeste porte le GUID d’ensemble, les identités, les compteurs SQL et les SHA-256. Le mot de passe d’identité et `SharedDataKey` sont à sauvegarder séparément. Protéger les archives, qui contiennent aussi comptes et jetons. Les médias, artefacts et sauvegardes de flows nécessitent leur sauvegarde de stockage habituelle.

Pour un ensemble coordonné : mettre l’installation en maintenance, arrêter agents et trois API, puis sauvegarder chaque pair avec le même `BACKUP_SET` (un nouveau GUID pour cet ensemble). Conserver son image applicative et sa configuration. Le verrou d’identité refuse la sauvegarde d’un pair démarré.

```sh
dotnet Encoding.Api.dll backup-peer \
  --data-directory "$PEER_DATA" --cluster-id "$CLUSTER_ID" \
  --database "$PEER_CONNECTION" \
  --key-directory "$PLUGIN_KEYS" --backup-set "$BACKUP_SET" \
  --output "$ARCHIVE_DIRECTORY"
```

Pour restaurer, utiliser des bases et répertoires cibles vides et les mêmes secrets. Le binaire doit correspondre à la sauvegarde. Restaurer chaque archive dans son pair d’origine, avec son endpoint et son identité ; ne jamais démarrer simultanément l’original et sa copie.

```sh
dotnet Encoding.Api.dll restore-peer \
  --data-directory "$EMPTY_PEER_DATA" --cluster-id "$CLUSTER_ID" \
  --database "$EMPTY_PEER_CONNECTION" \
  --key-directory "$EMPTY_PLUGIN_KEYS" --backup-set "$BACKUP_SET" \
  --input "$ARCHIVE_DIRECTORY"
```

La restauration vérifie l’ensemble et les empreintes avant la copie, refuse une cible peuplée et contrôle les compteurs. Redémarrer les trois pairs avec `Bootstrap=false`, attendre quorum, projection prête et convergence des révisions avant de relancer les agents. Un pair restauré plus ancien peut rejoindre une majorité existante et rattraper ses entrées. Un ensemble complet doit provenir du même arrêt coordonné ; ne pas mélanger des dates ou identités.

En cas de quorum perdu, rétablir assez de membres avec leurs propres volumes. Ne jamais effacer le WAL ni forcer un nouveau bootstrap sur un membre isolé. `GET /api/v1/peer-cluster/recovery` expose les fichiers persistants à conserver.

## Rotation d’un certificat

Procéder un pair à la fois, avec une majorité disponible.

1. Sur le pair concerné : `POST /api/v1/peer-cluster/certificate/prepare`. Conserver `certificatePem` et `thumbprint` ; la clé privée suivante reste locale et chiffrée.
2. Sur le leader : `POST /api/v1/peer-cluster/peers/{peerId}/certificate`, corps `{ "certificatePem": "…", "fingerprint": "…" }`. Attendre que les trois index engagés aient reçu cette entrée. Ancien et nouveau certificats sont alors acceptés.
3. Arrêter le pair, puis activer la clé préparée :

```sh
dotnet Encoding.Api.dll activate-peer-certificate \
  --data-directory "$PEER_DATA" --cluster-id "$CLUSTER_ID" \
  --approved-fingerprint "$NEW_FINGERPRINT"
```

4. Redémarrer ce pair avec ses volumes existants ; vérifier son identité et son rattrapage.
5. Sur le leader : `POST /api/v1/peer-cluster/peers/{peerId}/certificate/complete`. Le serveur vérifie que le pair présente la nouvelle empreinte avant d’engager le retrait de l’ancien certificat. Attendre la convergence avant la rotation suivante.

Le mot de passe `PeerCluster__IdentityPassword` doit être fourni aux commandes. La rotation conserve `peerId` et la membership. Un certificat non approuvé reste refusé, même s’il annonce le même identifiant.

## Mises à jour

La politique initiale impose la même version sur tous les pairs et une mise à jour coordonnée. Arrêter les entrées et agents, laisser finir ou annuler proprement les tâches, arrêter les trois API, sauvegarder l’ensemble, mettre à jour les trois images, puis redémarrer avec les volumes existants et `Bootstrap=false`. Cette procédure conserve le WAL **uniquement si protocole et schéma sont compatibles** ; vérifier les notes de version avant toute mise à jour.

Les changements incompatibles sont bloqués par le contrôle de protocole/empreinte (HTTP 426). Une version qui change le schéma doit fournir sa procédure de migration hors ligne et ses essais de restauration avant déploiement. Ne pas tenter une migration en ligne ni mélanger N et N-1. Un retour à la version précédente exige la restauration de l’ensemble coordonné sous son binaire d’origine, avec les agents arrêtés ; des effets externes postérieurs à la sauvegarde ne sont pas annulés par la restauration SQL.

## Scénarios de validation

| Incident | Attendu |
| --- | --- |
| Leader isolé, deux pairs joignables | Nouveau leader ; la majorité accepte les écritures, l’ancien leader les refuse. |
| Deux pairs perdus | Lectures locales possibles ; aucune écriture partagée ou attribution confirmée. |
| Retour d’un pair en retard | Rattrapage du WAL et convergence sans nouveau bootstrap. |
| Élection pendant une tâche | Ancien terme refusé ; reprise sous nouvelle époque, sans bail concurrent. |
| Effet externe non idempotent interrompu | Échec explicite et relance manuelle. |
| Perte de la seule source directe | Échec explicite et réservation libérée ; aucun traitement d’une copie inventée. |
| Redémarrage complet | Membership et projections reconstruites depuis instantané + WAL. |
| Restauration des trois pairs | Identités conservées ; écriture postérieure au dernier instantané retrouvée. |
| Mauvais protocole/schéma | Refus avant traitement du message. |
| Rotation | Nouveau certificat accepté durant chevauchement, ancien retiré après confirmation. |

```sh
dotnet test backend/tests/Encoding.Runtime.Tests/Encoding.Runtime.Tests.csproj \
  --filter 'FullyQualifiedName~Peer'
```

Exécuter les tests runtime SQLite avec les outils médias de `backend/tests/Dockerfile`. Voir [monitoring.md](monitoring.md) et [peer-cluster-alerts.yml](peer-cluster-alerts.yml) pour la supervision et [le suivi](peer-cluster-production-todo.md) pour la clôture des six points.
