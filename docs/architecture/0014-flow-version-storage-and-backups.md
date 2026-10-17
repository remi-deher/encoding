# 0014 — Versions de flow et sauvegardes JSON

## Stockage

La migration 0009 crée flows, flow_versions et flow_backup_jobs. Une version validée est immuable (contrainte de clé et trigger interdisant modification/suppression). Le document portable est conservé en jsonb. L’identité stable du flow est séparée de sa version courante.

PUT /api/v1/flows/{id} reçoit expectedVersion et definition. La nouvelle version est expectedVersion + 1. Le dépôt verrouille la ligne du flow et compare sa version courante avant publication. Une concurrence obsolète reçoit HTTP 409. La répétition du même document à la même version est reconnue sans modifier la version courante ; un contenu différent est rejeté.

GET /api/v1/flows liste les versions courantes avec leur état de sauvegarde ; GET /api/v1/flows/{id} lit la version courante ; GET /api/v1/flows/{id}/versions/{version} lit une version précise. Aucun endpoint ne supprime un flow ou une version.

Cette étape enregistre uniquement des versions validées. La conservation de brouillons incomplets et une interface de comparaison des versions restent à ajouter.

## Sauvegarde séparée

La publication écrit le travail local de sauvegarde dans la même transaction SQLite. Chaque serveur recrée et traite sa file locale depuis les définitions réconciliées ; ces travaux ne sont pas une file commune.

Chaque fichier est nommé {id}.v{version}.json dans FlowBackups:Directory. Compose utilise /var/lib/encoding/flow-backups, monté sur le volume encoding-flow-backups, séparé du volume SQLite local.

Le fichier contient le document portable, sans wrapper ni code d’interface. Une écriture utilise un fichier temporaire unique dans le même dossier, Flush avec synchronisation sur disque, puis un déplacement sans écrasement. Un fichier existant est comparé par SHA-256 au JSON canonique de la version en base. S’il diffère, il est conservé pour diagnostic et le travail passe en failed.

Le statut pending/ready/failed est indépendant de la version. Un échec est retenté toutes les 30 secondes. Une sauvegarde réussie est revérifiée toutes les 10 minutes ; un fichier manquant est recréé depuis la version immuable. Les fichiers temporaires de l’écriture courante sont nettoyés ; aucune suppression des instantanés n’est programmée.

Avec le maillage SQLite, les définitions et les entrées de la file de sauvegarde sont réconciliées, mais le statut de sauvegarde reste propre à chaque serveur. Chaque serveur régénère et vérifie ses propres fichiers depuis les versions répliquées ; le même dossier n'a donc pas besoin d'être monté sur tous les hôtes. Un volume Docker local doit tout de même être sauvegardé si l'on veut conserver ce filet de sécurité indépendant de la base.

## Exécutions

La création d’une exécution exige une version déjà enregistrée. Le backend vérifie la définition fournie contre la version immuable et copie celle-ci dans le registre existant. Un document arbitraire ou modifié n’est pas accepté. Les anciennes tentatives sont conservées ; le futur moteur devra revalider leur compatibilité avant reprise.

## Interface

Le catalogue affiche les flows enregistrés, avec chargement/erreur/vide du gabarit commun ; les anciennes lignes d’exemple sont retirées. Les modèles de départ restent disponibles lors de la création. Les plugins externes absents doivent être retirés de ces modèles ou installés avant publication.

L’éditeur charge le document et les descripteurs de ses versions exactes. Une référence absente est conservée explicitement, sans supprimer sa configuration ou ses liaisons. L’enregistrement utilise le contrôle de version ; les identifiants générés ne réutilisent pas ceux des blocs chargés. L’état de sauvegarde est actualisé toutes les cinq secondes. Une navigation ou un rechargement avec modifications non enregistrées est protégé.

Les valeurs optionnelles null des descripteurs sont normalisées vers l’absence dans l’adaptateur Angular ; elles ne deviennent pas des bornes numériques artificielles. Les valeurs null de la configuration du flow sont conservées.

## Récupération et limites

Les fichiers JSON restent lisibles indépendamment de SQLite. Après restauration du service et du catalogue, les versions peuvent être réimportées par PUT dans l’ordre croissant avec leur identité originale et expectedVersion correspondant à la version précédente. L’API valide chaque document ; elle refuse tout conflit avec une version existante. Aucun import automatique ou remplacement de versions existantes n’est effectué.

La restauration guidée, une archive de catalogue/plugin historique, la rétention des sauvegardes et les brouillons ne sont pas implémentés. Aucun média n’est traité ou remplacé par cette étape.

Compilation .NET/Angular, lint, formatage et démarrage Docker sont vérifiés. Aucun test automatisé ou parcours UI n’est ajouté/exécuté.
