# 0016 — Bibliothèques, flows et découverte des fichiers

## Modèle retenu

Les flows sont créés et publiés indépendamment. Une bibliothèque reçoit un seul flow actif, référencé par identifiant et version publiée précise. Les mises à jour du flow ne changent pas silencieusement cette attribution. Les nœuds déclarent séparément leurs accès à la bibliothèque ; le répartiteur conserve ses contrôles de capacités et de baux.

Le tableau des flows ne propose plus de lancement. La bibliothèque propose l’attribution, la découverte et le traitement unitaire ou groupé des fichiers détectés. L’utilisateur ne saisit aucun chemin média. Le chemin relatif est calculé à la découverte et permet de résoudre les racines différentes des nœuds.

## Stockage et API

La migration 0012 ajoute une révision d’attribution durable sur la bibliothèque, la référence au flow, les demandes de découverte et l’inventaire. La révision augmente même lorsqu’une attribution est retirée pour éviter les écritures fondées sur une ancienne configuration.

- GET /api/v1/libraries/{id}/workflow : attribution, révision, dernière découverte et fichiers.
- PUT /api/v1/libraries/{id}/flow : flowId, version, expectedRevision. Deux valeurs null retirent l’attribution.
- POST /api/v1/libraries/{id}/discover : demande durable, idempotente lorsqu’une découverte est déjà active.
- POST /api/v1/libraries/{id}/executions : fileIds et assignmentRevision. Le backend retrouve les chemins et la définition publiée ; aucune définition ou racine n’est fournie par le client.

Les anciens endpoints de création manuelle d’exécution retournent HTTP 410 et indiquent le lancement depuis la bibliothèque. Annulation, relance et suivi existants restent disponibles.

L’attribution et la mise en file utilisent un verrou sur la bibliothèque. Le contrôle de révision empêche de lancer une sélection avec une attribution modifiée entretemps. La création du registre et du travail réutilise RuntimeRepository.EnqueueInTransactionAsync dans la transaction commune. Les doublons déjà en attente ou en cours sont ignorés explicitement et comptés dans la réponse.

## Découverte distribuée

Un agent enrôlé et présent, doté d’un accès direct actif en lecture, prend une demande avec SKIP LOCKED. Les nœuds ne possédant qu’un accès par transfert ne peuvent pas énumérer le stockage ; leur nœud source direct le fait. Une fonction serveur sans capacité de traitement peut également effectuer la découverte.

Le bail dure 60 secondes, renouvelé toutes les 15 secondes. L’époque et la racine d’accès capturée sont vérifiées à la remise de l’inventaire. Un changement de montage, une désactivation ou une perte de bail empêche une ancienne remise. Une interruption laisse une demande reprenable par un autre nœud autorisé.

L’agent parcourt les dossiers, exclut les liens symboliques et les dossiers .encoding*, puis filtre les extensions selon le type du flow attribué. Le contrôle des métadonnées reste effectué par le moteur avant toute transformation. La découverte n’exécute pas le flow et ne modifie pas les médias.

Un succès remplace atomiquement l’inventaire visible ; les fichiers toujours présents conservent leurs identifiants. Un échec conserve l’inventaire précédent et affiche son motif. La limite actuelle est 10 000 fichiers par découverte : son dépassement échoue explicitement, sans inventaire tronqué. La surveillance automatique des dossiers et la découverte paginée de très grandes bibliothèques restent des évolutions futures.

## Interface et impacts communs

Les formulaires utilisent RecordForm et les fichiers utilisent DataTable : actions de liste pour découvrir, actions de ligne pour traiter, sélection pour les traitements groupés. ClusterApi porte les contrats de bibliothèque partagés. Le catalogue continue à gérer uniquement les définitions de flows.

Le dossier local media-test reste monté sous /media/test. La bibliothèque d’essai reçoit le flow d’inspection publié, sans traitement automatique. Un média doit être ajouté dans le dossier puis découvert depuis la bibliothèque.
