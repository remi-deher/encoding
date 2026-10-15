# 0041 — États de nœud, compatibilité de versions, identités en doublon

Statut : implémentée (périmètre ci-dessous). Complète 0038 à 0040 ; décisions de Rémi du 2026-10-15 (propositions 1, 4 et 5 du fil « Gestion simple des clusters »). L'ADR 0042 reste réservé au périmètre répliqué entre clusters.

## États

Un nœud a un état de cycle de vie dérivé, le plus restrictif l'emporte : `Quarantined` > `Incompatible` > `Draining` > `Offline` > `Online`.

- **Vidange** (`Draining`) : action d'administrateur (`PUT /api/v1/nodes/{id}/draining`). Le nœud termine ses tâches en cours et n'en reçoit plus ; reprise par la même action.
- **Incompatible** : posé à chaque présence quand la version de protocole annoncée est hors de la plage acceptée par le cluster. Il se lève tout seul quand le nœud est mis à jour.
- **Quarantaine** : posée automatiquement en cas d'identité probablement dupliquée (ci-dessous) ; levée par un administrateur (`POST /api/v1/nodes/{id}/quarantine/clear`).

La colonne générée `cluster_nodes.accepts_work` (migration 0033) vaut faux dans ces trois cas. `RuntimeRepository.ClaimAsync` et `LibraryWorkflowRepository.ClaimScanAsync` l'exigent : un nœud qui n'accepte pas de travail n'obtient aucun bail, sans toucher à ceux déjà en cours.

## Compatibilité

`ClusterProtocol.Current = 1`, `Minimum = 0`. Le nœud envoie sa version de protocole dans chaque présence ; une version absente compte pour 0. Limite assumée : seule la version de protocole est contrôlée aujourd'hui. La plage de schéma de base de données et la règle « serveurs avant workers » sont reportées ; les migrations restent additives en attendant.

## Identités en doublon

Chaque processus d'agent tire un nonce de session aléatoire et l'envoie avec sa présence. Un changement de nonce unique est un redémarrage normal. Une alternance a → b → a en moins de 10 minutes signifie que deux processus utilisent la même identité : quarantaine. Faux positifs possibles si un nœud redémarre plusieurs fois très vite ; la levée est un clic.

## Non implémenté

L'état « Silencieux » (nœud en ligne mais sans progrès) n'existe pas.
