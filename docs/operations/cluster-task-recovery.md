# Transfert et reprise de tâches entre serveurs

## Prérequis et invariants

Chaque serveur conserve son SQLite local. Les baux sont locaux au propriétaire ; le maillage signé transporte les décisions et les checkpoints, jamais une base partagée. Une partition ne suspend pas les calculs déjà possédés. Un simple délai réseau ne permet aucune reprise.

À deux serveurs, **arrêter réellement l’installation source et tous ses workers avant de déclarer sa neutralisation**. L’attestation administrative ne coupe aucun processus à distance. Ne jamais attester cet arrêt sur la seule base d’un ping absent. À trois serveurs ou plus, la reprise reste refusée tant que le futur module de décision de reprise n’existe pas.

## Procédure

1. Lire `GET /api/v1/cluster/tasks/status` et la propriété de l’exécution. Les commandes de gestion exigent le rôle administrateur.
2. Pour un transfert coopératif, appeler sur le propriétaire `POST /api/v1/cluster/tasks/{executionId}/move` avec `{"targetPeerId":"…"}`. Le checkpoint doit être durable, les accès et capacités de la cible doivent convenir. La cible vérifie taille et SHA-256 des fichiers puis accuse réception ; seul l’événement d’activation transfère la propriété. Un accusé perdu ne vaut pas activation.
3. Si la source est définitivement arrêtée et ses workers neutralisés, appeler sur le survivant `POST /api/v1/cluster/tasks/peers/{peerId}/neutralize` avec `{"confirmedStopped":true,"reason":"motif explicite de huit caractères minimum"}`. Attendre au moins 135 secondes à partir de l’attestation enregistrée par le serveur.
4. Appeler `POST /api/v1/cluster/tasks/{executionId}/recover` sur le survivant. Sans checkpoint signé actuel, fichiers vérifiés, étapes rejouables et capacités locales adaptées, la reprise est refusée. Ne pas contourner ce refus par une modification SQL.
5. Au retour de la source, la laisser synchroniser son journal tout en restant neutralisée. Depuis le serveur ayant attesté l’arrêt, appeler `POST /api/v1/cluster/tasks/peers/{peerId}/readmit`. La réponse directe signée, le nonce et le rattrapage du journal sont vérifiés avant réadmission.

Le mode `server` du composant `nodes-handoff` utilise `targetServerPeerId` (identifiant visible dans le statut du serveur cible). Les anciens workers ne déclarant pas le runtime 1.8 ne peuvent pas exécuter ce segment. Les commandes existantes annuler/reprendre/réessayer sont transmises au propriétaire ; une partition donne un refus visible.

## Limites opérationnelles

Les sorties restent temporaires jusqu’à validation et publication ; les sources ne sont pas remplacées par défaut. Les checkpoints précédant une publication ou des effets non rejouables sont refusés. Les caches distants sans travail local ne sont pas automatiquement purgés : surveiller leur volume. Le gossip de présence a une provenance et une expiration ; il ne constitue jamais une preuve de neutralisation. Un verrou de protection de source ne doit pas être supprimé sans vérifier l’arrêt de l’ancien écrivain.

Voir [ADR 0016](../architecture/adr/0016-transferts-et-reprise-manuelle.md) et [le point de reprise de développement](../testing/cluster-implementation-handoff.md).
