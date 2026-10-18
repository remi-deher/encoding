# 0066 — Sélection dynamique du nœud selon l’accès direct à la bibliothèque

Statut : implémentée. Plugin Nœuds 1.3.0.

## Contexte et besoin

Dans un cluster hétérogène de nœuds, les bibliothèques multimédias partagées peuvent être accessibles selon deux modes distincts déclarés par chaque nœud (`0016`) :
1. **Accès direct** : le nœud dispose d'un point de montage local (`local_path`) avec accès direct aux fichiers sur disque (ex. serveur de stockage TrueNAS / ZFS).
2. **Transfert vers un worker** : le nœud distant ne possède pas d'accès direct local au média et s'appuie sur le canal WebDAV pour se faire transférer une copie de travail par le nœud source.

Pour les entrées spécialisées telles que `base-input-video` (`0006`), une analyse approfondie (`ffprobe` / MediaInfo) est effectuée dès l'étape d'entrée. Si un worker distant en mode `transfer` réclame l'exécution dès cette première étape, le nœud source est contraint d'extraire, hasher et téléverser l'intégralité du fichier (pouvant atteindre 50 à 80 Go), tandis que le worker distant le télécharge via le réseau uniquement pour lire quelques kilo-octets d'en-têtes multimédias.

Le répartiteur vérifie déjà l’accès configuré à la bibliothèque lors de l’attribution, mais le flow ne pouvait pas exprimer formellement : « attribue ce segment exclusivement à un worker disposant d'un accès direct local au fichier ».

## Décision

Ajout du composant `nodes-require-direct-access` au sein du plugin intégré `nodes` (version 1.3.0) :

- **Identifiant :** `nodes-require-direct-access`
- **Plugin :** `nodes`
- **Catégorie :** `action`
- **Sortie unique :** `success` (« Suite »)
- **Configuration :**
  - `mode` (liste déroulante, valeur par défaut `strict`) :
    - `strict` : Le segment courant (depuis l'entrée ou le dernier relais jusqu'au prochain `nodes-handoff`) DOIT être attribué à un worker disposant d'un accès `direct`, activé, lisible et doté d'un `local_path`. Les workers en mode `transfer` sont inéligibles. Sans worker direct disponible, la tâche reste en file d'attente (`queued`) dans le cluster.
    - `prefer` : Préférence pour l'accès direct. Un worker distant en mode `transfer` ne peut réclamer la tâche que si aucun worker avec accès direct n'est actuellement en ligne et actif pour cette bibliothèque.

## Fonctionnement du runtime et des relais

1. **Éligibilité de segment (`RuntimeEligibility.RequiresDirectAccess`) :**
   Lorsqu'un worker interroge le serveur pour réclamer une exécution (`ClaimAvailableAsync`), le répartiteur inspecte le segment de flow applicable (résolu par `RuntimeHandoffPolicy.Segment`). Si le segment contient `nodes-require-direct-access`, la requête SQL d'accès bibliothèque exige formellement `a.mode = 'direct'`. Tout worker ne disposant que d'un accès `transfer` est immédiatement exclu de l'attribution pour ce segment.
2. **Scan local immédiat :**
   Grâce à cette contrainte, le segment d'entrée est réclamé par le nœud de stockage local (ex. TrueNAS). L'analyse `base-input-video` s'exécute localement en quelques centaines de millisecondes sans aucun transfert réseau.
3. **Relais vers un worker de calcul (`nodes-handoff`) :**
   Une fois le scan terminé et les métadonnées enregistrées, le flow peut évaluer les pistes et codecs (via `conditions-match` ou l'arbre de règles) :
   - Si aucune transformation n'est nécessaire (ex. le fichier est déjà encodé en HEVC), le flow peut se terminer directement sur le nœud local sans avoir déplacé le moindre octet.
   - Si un transcodage lourd est requis, le flow passe par `nodes-handoff` pour céder la suite à un worker distant adapté (ex. worker équipé d'un GPU NVENC ou AV1).
   - Le segment suivant ne comportant pas la contrainte `nodes-require-direct-access`, le worker distant en mode `transfer` devient éligible et télécharge alors le fichier de manière justifiée.
4. **Relais vers soi-même :**
   Si la cible d'un relais s'avère être le nœud courant déjà détenteur du bail, le flux évite tout transfert circulaire inutile et poursuit l'exécution locale.
5. **Simulation de flow :**
   Dans le simulateur (`FlowSimulator`), le bloc est validé comme contrainte déclarative de placement et résout la sortie `success` avec la mention correspondante dans l'historique simulé.

## Compatibilité

- Le plugin `nodes` passe en version `1.3.0`.
- Les blocs historiques `nodes-run-on-node`, `nodes-require-capability`, `nodes-handoff` et `nodes-retry-on-worker-loss` conservent leur compatibilité avec les versions `1.0.0`, `1.1.0` et `1.2.0`.
- Le nouveau bloc `nodes-require-direct-access` exige la version `1.3.0` du plugin sur les workers participants.
