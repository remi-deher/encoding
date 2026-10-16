# Déployer le prototype de cluster pair à pair

Le mode `PeerCluster` est une préversion à activer explicitement. Il valide l’identité cryptographique des serveurs, le rattachement Raft, la présence distribuée des nœuds et le quorum. Il ne réplique pas encore les données métier de l’application et n’est pas une solution multi-serveur de production.

## Ce qui est partagé aujourd’hui

- L’identité de cluster et son nom sont communs aux pairs. Le nom est confirmé par Raft.
- Le catalogue des bibliothèques (identifiant, nom, date de création et activation), le flow assigné à chaque bibliothèque, les versions publiées des flows et leur brouillon courant sont inscrits dans Raft puis appliqués à la base locale de chaque pair. Au premier démarrage, le serveur `Bootstrap` importe ces données avant l’arrivée des autres pairs.
- Les serveurs votants et leur configuration sont conservés dans le stockage Raft local de chaque serveur.
- Les nœuds workers publient leur état par gossip signé. `GET /api/v1/nodes`, la page Nœuds et la vue Cluster combinent ces observations; leur présence peut diverger brièvement pendant une partition.
- Une révocation de pair est inscrite dans Raft après son retrait de la membership et s’applique aux pairs qui restent membres.
- Les nouvelles tâches et les écritures runtime des workers exigent un bail de leader avec quorum. L’époque remise à un worker combine le terme Raft et le compteur de tentative de la tâche; après une nouvelle élection, les requêtes portant l’ancien terme sont refusées, même si leur bail SQL local n’a pas encore expiré.

Les profils d’encodage, scripts, modèles, packs, configurations de plugins, accès propres à chaque nœud, paramètres des workers, files et résultats d’exécution restent locaux. L’import groupé des flows est désactivé en mode pair à pair tant qu’il ne passe pas par une commande Raft atomique. Le fencing par terme rejette les prochains appels d’un ancien worker après une élection, mais ne peut pas annuler un effet externe déjà lancé par un plugin. N’activez pas ce mode sur plusieurs serveurs qui doivent gérer un même état métier.

## Préparer chaque serveur

Attribuez un GUID identique à `PeerCluster:ClusterId` sur tous les serveurs. Choisissez une URL HTTPS racine et stable pour chacun (`PeerCluster:PublicEndpoint`), joignable depuis les autres serveurs. Le port pair par défaut est 8443 (`PeerCluster:ListenPort`). Le certificat de chaque pair inclut l’hôte de son URL.

Configurez sur chaque serveur :

```text
PeerCluster__Enabled=true
PeerCluster__ClusterId=<GUID commun>
PeerCluster__Bootstrap=false
PeerCluster__PublicEndpoint=https://<nom-ou-ip-du-serveur>:8443/
PeerCluster__ListenPort=8443
PeerCluster__IdentityPassword=<secret propre à ce serveur, 32 caractères minimum>
PeerCluster__DataDirectory=/var/lib/encoding-node/peer-cluster
```

Utilisez `PeerCluster__Bootstrap=true` sur un seul premier serveur, au premier démarrage seulement. Laissez cette valeur à `false` pour tous les autres. Chaque serveur doit avoir un dossier de données persistant qui lui est propre. Ne montez jamais le même dossier Raft en écriture sur plusieurs serveurs.

Générez un secret différent pour chaque pair, conservez-le dans le gestionnaire de secrets de l’installation et ne le placez pas dans le dépôt Git :

```sh
openssl rand -base64 48
```

Ce secret chiffre le fichier de clé privée `peer-identity.pfx`; le fichier et ses métadonnées sont protégés par les permissions locales du volume. Une perte du secret empêche le redémarrage avec cette identité. Sauvegardez le secret avec les sauvegardes chiffrées de l’installation.

Pour Docker Compose, les variables `PEER_CLUSTER_*` correspondantes sont déjà câblées. Le port 8443 est lié à `127.0.0.1` par défaut; pour des pairs distants, définissez `PEER_CLUSTER_BIND` sur une adresse joignable et limitez ce port au pare-feu aux seuls serveurs du cluster. Les routes ordinaires de l’API ne sont pas exposées sur ce port : seuls Raft et le gossip de présence y répondent, après authentification TLS mutuelle.

## Approuver et ajouter un serveur

1. Démarrez le premier pair et vérifiez `GET /api/v1/peer-cluster/identity` sur son port API normal. Ce point de terminaison fournit `clusterId`, `peerId`, `endpoint`, le certificat public et `thumbprint` (empreinte SHA-256). Obtenez la même information pour le serveur à ajouter. Comparez les empreintes par un canal d’administration fiable.
2. Sur chacun des deux serveurs, un administrateur approuve l’autre avec `POST /api/v1/peer-cluster/peers`. Envoyez les champs `peerId`, `clusterId`, `endpoint`, `certificatePem` et `fingerprint`; cette dernière valeur reprend le `thumbprint` SHA-256 de la réponse d’identité. L’approbation est réciproque et doit être répétée pour chaque paire de serveurs. Une découverte ou un GUID identique n’accorde jamais la confiance automatiquement.
3. Pour une nouvelle base de pair, initialisez d’abord sa base locale vide depuis la base faisant autorité avec la commande `initialize-peer-database` ci-dessous. Pour un pair qui revient après une panne, conservez et réutilisez son propre volume et sa base restaurés ; ne relancez pas cette copie.
4. Depuis l’API du leader Raft, appelez `POST /api/v1/peer-cluster/peers/{peerId}/membership`. Le pair doit déjà être approuvé sur les deux serveurs. Ajoutez un seul pair à la fois et attendez la confirmation avant d’ajouter le suivant.
5. Sur chaque serveur, vérifiez `GET /api/v1/peer-cluster/consensus` : même membership, index engagé à jour et un leader avec `hasQuorum=true`. Comparez `lastCommittedEntryIndex` sur le leader et le nouveau pair avant de lui confier des opérations. Vérifiez ensuite `GET /api/v1/peer-cluster` et `GET /api/v1/nodes` pour la présence des pairs et des workers.

`POST /membership` attend maintenant la confirmation du changement Raft. La réponse ne garantit pas que le nouvel entrant a terminé son rattrapage ; l’index engagé doit être contrôlé séparément. `GET /api/v1/peer-cluster/recovery` indique les fichiers persistants à préserver et l’action conseillée après une panne. Pour retirer un pair, utilisez le leader courant ; l’API refuse l’auto-révocation et la suppression du dernier membre.

Les routes d’administration requièrent la session administrateur et la protection CSRF de l’API. N’exposez pas les jetons de session dans les journaux ni dans un script partagé.

Pour retirer un serveur, appelez `DELETE /api/v1/peer-cluster/peers/{peerId}` sur le leader. Le retrait Raft et la révocation distribuée demandent un quorum. Si le quorum manque, l’API retourne une erreur et la révocation doit être réessayée après rétablissement du quorum. Pour réintégrer un pair révoqué, approuvez à nouveau son certificat sur les membres restants, puis demandez la réactivation au leader avant de le réajouter à la membership.

## Sauvegarde et migration

Sauvegardez ensemble, pour chaque serveur, sa base de données locale, son volume de données, son `PeerCluster:IdentityPassword` et les clés locales de protection des secrets. Le volume pair contient notamment `peer-identity.pfx`, `peer-identity.json`, `peer-incarnation.txt`, `trusted-peers.json`, `raft/membership`, `raft/wal` et les instantanés Raft. Ne restaurez pas les fichiers Raft d’un pair sur un autre pair et ne supprimez pas le WAL pour « réparer » un membre.

Choisissez explicitement une seule base comme source faisant autorité. Pour initialiser la base vide d’un nouveau pair, arrêtez les écritures sur la source puis lancez depuis le conteneur/API :

```sh
dotnet Encoding.Api.dll initialize-peer-database \
  --authority-provider postgres --authority "$AUTHORITY_CONNECTION" \
  --target-provider postgres --target "$NEW_PEER_CONNECTION"
```

La commande migre le schéma cible à la version de l’application, refuse les tables applicatives déjà peuplées, copie toutes les données en transaction et vérifie les nombres de lignes. La source reste intacte. Les chaînes de connexion contiennent des secrets : ne les inscrivez pas dans les scripts versionnés ni dans les journaux. Utilisez `sqlite` comme fournisseur si les deux bases locales sont SQLite. Cette procédure initialise un nouveau pair, elle ne fusionne pas les bases existantes.

La migration automatique de plusieurs bases existantes, les instantanés complets de l’application et la restauration coordonnée entre pairs ne sont pas livrés. Une copie de départ n’empêche pas la divergence ultérieure des tables qui ne sont pas répliquées. Gardez ces écritures hors des pairs secondaires jusqu’à la livraison du point 1 documenté dans [peer-cluster-production-todo.md](peer-cluster-production-todo.md).

## Validation disponible

Le test d’intégration démarre trois API indépendantes et vérifie l’approbation mutuelle, le rattachement des deux pairs, la membership à trois votes, la convergence du nom du cluster et des présences, le refus d’un client TLS sans certificat, l’isolation du port pair et la diffusion de révocation après retrait :

```sh
dotnet test backend/tests/Encoding.Runtime.Tests/Encoding.Runtime.Tests.csproj \
  --filter FullyQualifiedName~PeerCluster
```

Cette validation ne couvre pas encore la réplication des autres données métier, le fencing d’un effet externe déjà lancé, la reprise après restauration complète, les partitions réseau prolongées ni la mise à niveau entre versions différentes.
