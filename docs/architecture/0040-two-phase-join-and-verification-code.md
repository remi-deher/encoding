# 0040 — Rattachement en deux phases, code de vérification, première liaison

Statut : implémentée. Complète 0038 et 0039 ; décisions de Rémi du 2026-10-15 (propositions 2 et 3 du fil « Gestion simple des clusters »). Les ADR 0041 (états de nœud, compatibilité de versions, identités en doublon) et 0042 (périmètre répliqué entre clusters) sont réservés pour la suite.

## Changer de cluster en deux phases

Avant : un nœud qui changeait de cluster quittait d'abord l'ancien. Un échec en route le laissait orphelin.

Maintenant, depuis un nœud déjà rattaché :

1. **Mise de côté.** L'identité actuelle (identifiant, jeton, cluster) est conservée telle quelle dans l'identité locale. Une identité neuve attend l'accueil du nouveau cluster, avec une échéance de 12 minutes (la clé de jumelage vit 10 minutes). Pendant l'attente, le nœud continue d'envoyer sa présence à l'ancien cluster : ses baux et ses tâches ne sont pas touchés, et son serveur local reste en marche.
2. **Confirmation.** Dès que le nouveau cluster accepte le nœud (enrôlement direct réussi, ou premier signal de présence accepté après approbation), l'ancien cluster est prévenu au mieux (`leave`) et son identité est oubliée ; le serveur local s'arrête si le cluster d'accueil n'est pas celui du conteneur (0039).
3. **Retour arrière.** Si le cluster d'accueil est injoignable, refuse la demande ou n'approuve pas avant l'échéance, l'ancien rattachement est rétabli à l'identique, sans rien avoir envoyé à l'ancien cluster. La raison est conservée et affichée dans la console (« changement annulé : … »). Annuler à la main fait la même chose.

## Code de vérification

Le nœud et le cluster d'accueil calculent chacun six caractères (alphabet sans ambiguïté) à partir de l'identifiant du nœud et de l'empreinte SHA-256 de son jeton. Aucun secret ne circule en plus. La console du worker affiche son code ; la liste des demandes et la fiche de la demande, côté cluster d'accueil, affichent celui qu'il calcule. Un nœud inattendu ne peut pas avoir le même code. La comparaison reste humaine ; elle ne remplace ni l'approbation ni la clé de jumelage.

## Mode « accepter la première liaison »

Réglage explicite d'un administrateur du cluster d'accueil (`PUT /api/v1/nodes/first-link`), désactivé par défaut. La première demande de jumelage qui arrive pendant sa durée (1 à 60 minutes) est approuvée sans vérification ; le mode se consomme alors de façon atomique (une seule demande l'obtient, même avec plusieurs serveurs) et s'éteint. Il s'éteint aussi à l'expiration ou sur demande. L'activation et chaque acceptation sont journalisées avec le nom de l'administrateur. Il ne couvre pas l'enrôlement direct (qui a sa propre fenêtre) ; il couvrira les relations entre clusters (P3) avec la même règle. Migration 0032.

## Limites

- Quitter pendant un changement annule le changement ; la sortie volontaire immédiate (0039) reste inchangée pour un nœud sans changement en cours.
- Un nœud occupé (traitement en cours) refuse toujours le départ de son ancien cluster ; au moment de la confirmation, ce refus n'empêche plus la bascule : l'ancien cluster reprend les tâches à l'expiration des baux.
