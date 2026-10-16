# TODO de mise en production du cluster pair à pair

## À terminer : réplication complète de l’état métier (point 1)

Le prototype ne réplique actuellement que le catalogue des bibliothèques, les flows publiés, leurs brouillons et les assignations de flow aux bibliothèques. Le reste des tables applicatives peut encore diverger entre les bases locales.

Avant une mise en production multi-pair, concevoir et livrer des commandes Raft versionnées, avec validation de révision, projection locale transactionnelle, suppression répliquée et reprise par instantané pour au moins :

- profils d’encodage, modèles, scripts et packs de composants ;
- configurations et versions de plugins qui doivent être partagées ;
- paramètres de bibliothèques et toute configuration actuellement modifiable depuis l’interface ;
- files, états d’exécution, résultats et métadonnées d’artefacts, en définissant la propriété des fichiers et la reprise après perte d’un pair.

Les secrets, identifiants machine, chemins de stockage propres à un worker et paramètres propres à son installation doivent rester locaux, ou être partagés par un gestionnaire de secrets externe conçu pour cela. En revanche, la configuration d’accès d’une bibliothèque par nœud (nœuds de traitement, mode d’accès et nœud source) fait partie de l’état métier commun et doit converger. Un chemin direct reste une valeur propre au nœud auquel il s’applique. L’inventaire de chaque table doit préciser cette frontière avant d’ajouter sa réplication. La présence des pairs et le quorum ne suffisent pas à rendre les données métier convergentes.

Critères de sortie : toutes les écritures partagées passent par des commandes répliquées avec quorum ; une restauration d’instantané reconstruit les mêmes projections ; les nœuds refusent les écritures locales non répliquées ; les suppressions et reprises convergent ; les écarts sont détectables et réparables sans fusion automatique.

## Partiellement livré : fencing des tâches et écritures runtime (point 2)

- Les nouvelles attributions et les écritures runtime des workers exigent un bail de leader avec quorum.
- L’époque de fencing combine le terme Raft et le compteur de tentative local de la tâche. Après une élection, les requêtes avec l’ancien terme sont rejetées.
- Les tâches, leurs baux et leurs résultats ne sont pas encore répliqués comme état métier partagé. Le protocole ne peut pas annuler un effet externe déjà lancé par un plugin ; une exécution peut donc avoir produit un effet externe avant que son résultat soit refusé.

À terminer : répliquer l’état des tâches et des baux avec une reprise idempotente ; définir les règles de reprise d’un travail interrompu ; garantir que la publication du résultat converge ; rendre les effets externes idempotents ou documenter ceux qui ne peuvent pas l’être. Ce point dépend de la réplication générale du point 1.

## Partiellement livré : opérations sûres des pairs (point 3)

- Les changements de membership sont sérialisés, limités au leader avec quorum et attendent la confirmation de membership avant de répondre.
- L’ajout d’un membre déjà présent et le retrait d’un membre déjà absent sont idempotents. Le dernier membre ne peut pas être retiré et un pair ne peut pas révoquer sa propre identité via l’API.
- `GET /api/v1/peer-cluster/recovery` expose l’identité locale, les fichiers persistants attendus, l’état Raft, l’index engagé et la prochaine action de reprise conseillée.
- Le stockage Raft persistant comprend la membership, le WAL et les instantanés de `PeerStateMachine`. Un redémarrage doit réutiliser le volume original. Ne copiez jamais un volume Raft vers deux pairs et ne videz pas le WAL pour réparer une membership.

L’endpoint `/recovery` est un diagnostic ; il ne restaure pas automatiquement une sauvegarde. La cohérence coordonnée entre la base locale, l’identité et l’état Raft n’est pas encore outillée ni validée par un scénario de restauration. La réponse d’ajout confirme la membership, pas le rattrapage complet du nouveau pair. Avant de l’utiliser, comparer `lastCommittedEntryIndex` sur le leader et le nouveau pair via `/api/v1/peer-cluster/consensus`, puis vérifier les vues Cluster et Nœuds. Les tests de reprise, de restauration et de réintégration sont dans le point 6. Le point 1 reste nécessaire avant les opérations métier multi-pair.

## Outil livré, procédure à valider : migration depuis une source faisant autorité (point 4)

La commande `initialize-peer-database` copie toutes les tables applicatives d’une base source migrée vers une base cible vide, dans une transaction. Elle refuse une cible déjà peuplée, ne modifie pas la source et compare les compteurs après copie. Elle n’effectue aucune fusion.

Procédure pour un nouveau pair : arrêter les écritures applicatives sur la source durant la copie ; initialiser sa base locale depuis l’unique base désignée comme faisant autorité ; démarrer le nouveau serveur avec `PeerCluster:Bootstrap=false` ; approuver le certificat dans les deux sens ; ajouter le pair depuis le leader ; attendre le rattrapage Raft. Ne pas lancer cette commande lors d’un redémarrage ni sur une base de pair existante.

Cette copie donne un point de départ cohérent et son code est compilé. La procédure n’a pas encore été validée sur des bases réelles PostgreSQL et SQLite ni dans une jointure à trois pairs. Les tables qui ne sont pas encore répliquées restent sujettes à divergence après démarrage : voir le point 1 avant la production.

## Décision actuelle : stockage des médias (point 5)

Le stockage des fichiers reste sous la responsabilité du système déjà utilisé par l’installation. À la création d’une bibliothèque, l’administrateur choisit les nœuds qui peuvent la traiter puis déclare un mode d’accès pour chacun :

- **Direct** : le nœud voit les fichiers par son stockage accessible (disque, NFS, USB ou montage WebDAV). Son chemin est une valeur propre à ce nœud.
- **Indirect** : le nœud de traitement dépend d’un autre nœud qui a l’accès direct. La source est explicitement déclarée. Le mode `transfer` actuellement implémenté fournit au worker une copie locale temporaire et récupère le résultat ; un accès WebDAV servi par un nœud source reste un mécanisme de stockage/transport à préciser s’il est souhaité.

La liste des nœuds de traitement, leurs modes d’accès et la source choisie constituent la configuration partagée de la bibliothèque. Encoding conserve son rôle d’application et d’orchestrateur : il ne remplace pas le système de stockage et ne réplique pas les octets vidéo, audio ou image par Raft. Si aucun accès direct activé ne peut fournir le média à une tâche, ou si le transfert échoue, la tâche échoue immédiatement avec une erreur explicite ; elle n’attend pas le retour d’un nœud source.

La disponibilité d’une bibliothèque dépend donc de son stockage et des nœuds configurés pour y accéder. L’application ne doit pas inventer une copie ni traiter une autre version du fichier. Un stockage redondant ou une réplication de fichiers pourra être ajouté séparément si une exigence de continuité le demande.

## À construire et valider : exploitation et mises à jour (point 6)

Avant la production, exécuter les scénarios dans un environnement de trois pairs avec bases et volumes indépendants : partition d’un pair, perte du quorum, redémarrage du leader pendant une tâche, reprise d’un pair en retard, arrêt puis redémarrage complet, et restauration d’une sauvegarde dans un environnement isolé. Vérifier aussi le retrait/rattachement et la rotation des certificats. Ces scénarios ne sont pas encore exécutés.

Chaque scénario doit préciser l’état attendu du système, l’effet sur les tâches déjà lancées et la procédure de reprise. Les critères minimums sont : aucune nouvelle écriture durable ni attribution sans quorum ; rattrapage sans identité dupliquée après rétablissement ; détection claire d’un membre qui ne converge pas ; restauration vérifiée sans réutiliser les mêmes volumes Raft sur deux serveurs. Documenter les limites des effets externes déjà lancés par un plugin.

Une rotation de certificat pair à pair et une négociation de compatibilité de protocole ne sont pas encore implémentées ; il faut définir le chevauchement de confiance entre anciens et nouveaux certificats, puis ajouter l’échange de versions/protocoles avant de valider ces scénarios. Pour la première mise en production, imposer manuellement une version identique sur tous les pairs et privilégier une mise à jour coordonnée tant que la compatibilité en mode mixte n’a pas été démontrée. Les mises à jour progressives pourront être activées après des scénarios N/N-1 concluants. Ajouter aux alertes la perte de quorum, l’absence de progression de l’index engagé, un pair durablement en retard et le manque d’espace sur le volume Raft ; la vérification de quorum existe, mais ces métriques et alertes pair à pair restent à intégrer à la supervision.
