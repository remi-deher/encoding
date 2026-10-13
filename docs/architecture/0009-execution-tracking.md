# 0009 — Registre durable des exécutions

## Décision

PostgreSQL stocke les exécutions et leurs événements. Chaque tentative conserve son identifiant, sa source, les noms de bibliothèque/flow, la version et la définition JSON immuable du flow, ainsi que les horodatages serveur. Les nœuds serveur lisent le même registre.

Les états sont queued, running, cancelling, succeeded, failed et cancelled. Une progression de 100 % ne change pas l’état. Les transitions terminales sont explicites. Une annulation avant démarrage termine l’attente ; en cours, elle devient une demande que le moteur doit confirmer après arrêt. Une relance conserve l’échec et crée une tentative liée ; une contrainte empêche les relances concurrentes de la même tentative.

Une transition et son événement sont écrits dans une transaction. Le rapport interne du moteur fournit un identifiant d’événement et la révision attendue ; les mises à jour concurrentes obsolètes sont rejetées. Le rapport répété est reconnu. Les transitions terminales ne sont pas modifiables.

## API et interface

GET /api/v1/executions fournit les compteurs globaux, jusqu’à 200 exécutions (en attente/en cours prioritaires) et les 20 derniers événements, dans un instantané transactionnel cohérent. GET /api/v1/executions/{id}/events fournit la chronologie de la tentative. La page Tâches et l’accueil utilisent le même service Angular avec actualisation toutes les cinq secondes, conservation des dernières valeurs en cas d’erreur et signalement des données obsolètes.

POST /api/v1/executions enregistre une attente avec un identifiant fourni par l’appelant, une version positive et un objet JSON. Un doublon d’identifiant est rejeté explicitement. Cet endpoint ne valide pas encore la sémantique du graphe et ne lance aucun traitement. Les endpoints cancel et retry écrivent les demandes opérateur dans le registre.

## Frontière avec le moteur

Ce registre suit les traitements ; il n’attribue pas de tâches. RecordAsync est une API interne réservée au futur orchestrateur, sans endpoint public de progression ou de réussite. Avant de l’appeler, ce moteur devra contrôler les composants du flow, l’éligibilité et les accès du worker, les baux, le verrouillage du média et la validation de sortie. Le registre ne publie ni ne remplace aucun média.

Aucun worker actuel ne lance de flow ou n’alimente encore les rapports d’exécution. Les attentes enregistrées restent en attente. Les relances et annulations sont des écritures durables ; une demande d’arrêt doit être consommée par le moteur lorsqu’il sera disponible.

Les mesures de taille avant/après et les instantanés de métadonnées seront fournis par le scanner et le moteur ; les économies restent inconnues à ce stade.
