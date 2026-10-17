# 0033 — Attentes durables et gestion du temps dans Base

Statut : implémentée (décisions de Rémi : reprise à l'étape suivante, délai maximal facultatif, sans sortie « Délai dépassé » reliée l'exécution échoue, média réservé dans cette première version).

## Besoin

Un flow doit pouvoir s'arrêter sans bloquer un worker ni un emplacement de traitement, puis reprendre à l'étape suivante quand une condition est remplie : un délai écoulé, une heure atteinte, la fin d'une lecture Plex/Jellyfin, l'état d'un autre bloc, d'un worker ou le nombre de tâches. Aujourd'hui seul `base-defer-processing` existe : il termine l'exécution (`deferred`) et la relance depuis le début après 1 min à 24 h.

## Décision

Le plugin `base` fournit un composant **Attendre** (`base-wait`, action, sorties `resumed` et `timeout`). Il sépare le *mécanisme d'attente* (commun, durable) de la *condition de réveil* (déclarée dans le bloc, extensible). Le flow reprend à l'étape qui suit le bloc, avec le même contexte (variables, artefacts, scans, plan d'encodage) ; aucun bloc « attente » spécifique à un plugin n'est nécessaire.

### Condition de réveil (champ `wake`)

| Type | Évaluée par | Exemple |
|---|---|---|
| `timer` | serveur | délai en minutes (1 min à 7 jours) ; une heure du jour ou une date pourra s'ajouter |
| `nodeState` | serveur | un worker donné (ou tout worker) est en ligne / hors ligne / libre |
| `workload` | serveur | nombre de tâches en cours ou en file (global, bibliothèque, nœud) comparé à un seuil |
| `block` | un worker éligible | une condition d'un plugin (ex. `plex-check-playback`) renvoie l'une des sorties « remplies » choisies |

Les conditions `block` réutilisent n'importe quel composant de kind `condition` : le bloc stocke la référence du composant, sa configuration et la liste des sorties qui valent « remplie ». Le plugin n'a rien de nouveau à implémenter, et la validation (composant, version, sorties) est celle du moteur. Les types `timer`, `nodeState` et `workload` ne nécessitent aucun worker.

Paramètres communs : `pollSeconds` (intervalle de réévaluation, 15 s à 1 h), `maxWaitMinutes` (optionnel : vide = illimité, 1 min à 30 jours) et la sortie `timeout` (facultative ; si elle n'est pas reliée, un délai dépassé termine l'exécution en échec explicite plutôt que de continuer). Le bloc surveillé (`blockNodeId`) est une condition de plugin du même flow ; la validation refuse un identifiant inconnu, un bloc natif ou une sortie non déclarée.

### Mécanisme durable (modèle du relais 0021)

1. Au premier passage du bloc, le worker enregistre le checkpoint de l'étape (comme tout bloc) et demande l'attente dans la même transaction : création de la ligne `execution_waits`, bail révoqué, `lease_epoch + 1`, `available_at = 'infinity'`, réservation du média conservée (`lease_until = 'infinity'`). L'état public reste `running`, détail « en attente : <condition> » (même principe que `handoff_pending`, pas de nouvel état public). Le worker est libéré et son emplacement de traitement aussi : l'attente ne compte dans aucun plafond de bail.
2. `ExecutionWaitService` (sur chaque serveur) réclame les attentes dues avec `FOR UPDATE SKIP LOCKED` et une réclamation courte récupérable, évalue les conditions serveur, et décide : **remplie** → `available_at = now()`, l'exécution est réattribuée par le chemin de réclamation ordinaire (éligibilité, plafonds, calendrier du nœud, accès bibliothèque) ; **délai dépassé** → même chose avec la sortie `timeout` ; **ni l'un ni l'autre** → prochaine échéance (`next_check_at`).
3. Plusieurs serveurs n'aboutissent jamais à deux décisions : la décision est une mise à jour conditionnelle de la ligne d'attente (état `waiting` → `met|timeout|cancelled`), identique en cas de répétition.
4. Conditions `block` : le service marque l'attente « sonde due ». Un worker éligible la réclame par un bail court, exécute **uniquement** le composant de condition avec le contexte figé du checkpoint, puis rapporte la sortie avec son époque ; une sonde périmée est refusée. Aucun checkpoint d'étape n'est créé par une sonde. Si le worker perd son bail pendant la sonde, la tentative échoue comme toute tâche attribuée ; un bloc de relance bornée peut créer une nouvelle tentative.
5. À la reprise, le checkpoint enregistré à l'étape 1 porte déjà la suite normale (`resumed`). Pour `timeout`, le serveur ne forge pas de contexte : il réécrit uniquement `OutputId`/`NextNodeId` du dernier checkpoint vers la connexion `timeout` validée contre le flow publié, dans la même transaction que la décision.
6. L'annulation libère la réservation via le mécanisme existant. La perte du worker chargé d'une sonde clôt la tentative en échec et libère sa réservation ; elle ne réattribue pas la même sonde sous le même identifiant.

### Haute disponibilité et état des nœuds

Les états `nodeState` et `workload` sont lus dans la base du cluster (présence par battement de cœur, baux de `execution_work`). Tous les serveurs connectés à cette base évaluent la même attente ; une décision unique réveille l'exécution. La perte d'un serveur n'interrompt pas une attente durable. La perte du worker qui exécute une sonde fait échouer sa tentative. L'état d'un bloc se surveille par la sonde ci-dessus, pas par un abonnement à un worker précis.

### Plugins « événement » (extension)

Un plugin peut fournir de nouvelles conditions simplement en déclarant un composant `condition` ; l'attente s'y branche sans changement du moteur. Les services externes qui savent notifier (webhook Sonarr/Radarr…) pourront plus tard réveiller une attente par un événement entrant, sans sondage ; ce n'est pas inclus ici.

### Média pendant l'attente (à trancher)

Le média reste **réservé** pendant toute l'attente : aucune autre tâche ne le modifie et la source est intacte. Le champ `mediaPolicy` du bloc choisit ce qui arrive à la publication du résultat, et le dernier bloc Attendre traversé fait foi (`execution_work.media_policy`) :

- `keep` (défaut) : la politique de remplacement de la bibliothèque (0030) décide, comme avant.
- `beside` : copie à côté. La source n'est jamais remplacée, même si la bibliothèque l'autorise ; le résultat reste un fichier distinct sous `.encoding-results/<exécution>/`.
- `overwrite` : écrasement demandé par le flow. Il passe par le protocole 0030 (sauvegarde vérifiée, bascule atomique, période de grâce, restauration) et n'a lieu que si la bibliothèque a une politique de remplacement active ; sinon le résultat reste distinct et l'historique l'indique. Un flow ne peut donc jamais contourner la garde de la bibliothèque.

Seul le nœud d'accès direct à la bibliothèque écrit dans le média, comme pour toute publication. Limite : « à côté » n'écrit pas encore de fichier voisin visible par Plex ; le résultat reste dans `.encoding-results`.

## Implémentation

- Plugin Base 1.1.0 : `base-wait` (les composants existants restent résolubles en 1.0.0 par `compatiblePluginVersions`). Un agent qui n'annonce pas `plugin/base` 1.1.0 ne peut pas réclamer un flow qui contient le bloc.
- Migration `0029_flow_waits` : table `execution_waits` (état `waiting` / `met` / `timeout` / `cancelled`, échéances, compteur de sondes) et `execution_work.wait_probe_due`. L'attente met `available_at` à l'infini : aucune réclamation ordinaire n'est possible tant que la décision n'est pas prise.
- `RuntimeCheckpoint.Wait` porte la demande ; le serveur la recalcule depuis le flow publié et refuse toute différence, comme pour le relais. `RuntimeLease.Probe` désigne une réclamation de sonde (capacité du seul plugin surveillé, aucun accès bibliothèque, aucun plafond de segment).
- `ExecutionWaitService` (chaque serveur, toutes les 5 s) : `ProcessDueAsync` verrouille les attentes dues avec `SKIP LOCKED` et décide. `POST /runtime/nodes/{id}/executions/{id}/wait/probe` rend le résultat d'une sonde, protégé par l'époque du bail.
- Un échec de sonde (plugin injoignable) compte comme « non remplie » : l'attente continue jusqu'au délai maximal. La mise en attente n'évalue pas la condition avant de libérer le worker : la première évaluation a lieu à la première échéance du service.
- Limites connues : l'éditeur affiche tous les paramètres du bloc quel que soit le mode (les champs ne sont pas encore conditionnels) ; `blockNodeId` se saisit comme texte ; l'état « en attente » n'a pas de colonne dédiée dans la liste des exécutions (visible dans le détail et l'historique) ; les sondes comptent comme un bail ordinaire pendant leur courte exécution.

## Garanties à vérifier

Attente sans worker occupé, reprise à l'étape suivante avec contexte identique, minuterie après perte d'un serveur, deux serveurs simultanés (une seule décision), sonde avec époque périmée, aucun worker éligible, délai dépassé avec et sans sortie `timeout`, annulation pendant l'attente, réservation du média conservée, plafonds non consommés pendant l'attente, simulateur de flow (hypothèse explicite sur `resumed`/`timeout`).

## Impact

Contrats : `FlowStepContext` inchangé ; nouvelle demande d'attente et rapport de sonde dans les endpoints runtime. Migration additive (`execution_waits`, colonnes de sonde). Catalogue Base : nouveau composant (version du plugin à incrémenter avec `compatiblePluginVersions` comme pour 0021 ; un agent ancien ne peut pas réclamer un flow qui contient `base-wait`). Éditeur : champ `wake` rendu par le gabarit déclaratif (les types de champs existants suffisent, avec un champ de référence de composant à ajouter s'il n'existe pas). Simulateur 0023 et `base-defer-processing` : ce dernier reste inchangé.
