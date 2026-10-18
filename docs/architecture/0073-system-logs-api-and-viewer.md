# 0073 — API de logs système, flux temps réel et observabilité sans pollution Docker

Statut : décision d'architecture. Complète [0009](0009-execution-tracking.md), [0028](0028-execution-pipeline-command-logs.md), [0035](0035-ui-design-tokens-theme-and-cards.md), [0044](0044-single-node-management-page.md), [0049](0049-multi-server-shared-cluster.md), [0060](0060-api-tokens-and-ai-agent-endpoints.md) et [0061](0061-model-context-protocol-server.md).

---

## 1. Contexte & Problématique

Dans une installation distribuée d'Encoding (serveur et workers s'exécutant dans des conteneurs Docker sur plusieurs machines physiques ou virtuelles), les logs conteneur standard (`docker logs`) présentent plusieurs limites critiques pour l'exploitation quotidienne :

1. **Pollution et bruit de fond continu** :
   - Requêtes HTTP Kestrel récurrentes (`GET /api/... 200 OK`),
   - Heartbeats périodiques des workers vers le serveur (toutes les quelques secondes),
   - Consensus Raft et maintien du maillage inter-nœuds ([0049](0049-multi-server-shared-cluster.md)),
   - Sondes réseau DNS-SD / mDNS.
   En cas de problème (panne d'encodage matériel GPU, refus de jumelage, plantage d'un processus plugin), l'erreur est rapidement noyée sous des milliers de lignes de routine.
2. **Absence de centralisation multi-nœuds** :
   - L'opérateur doit ouvrir une session SSH ou un outil comme Portainer sur chaque machine pour inspecter chaque worker.
3. **Absence d'outillage de recherche et d'exploitation pour l'IA** :
   - Aucun moyen pour l'interface web ou pour les agents IA connectés au serveur MCP ([0061](0061-model-context-protocol-server.md)) d'inspecter l'état système et de diagnostiquer un incident sans demander à l'utilisateur de copier-coller des extraits de terminal.

### Distinction essentielle entre types de journaux

- **Journaux d'exécution de pipeline ([0028](0028-execution-pipeline-command-logs.md))** : déjà pris en charge dans `execution_steps.command_logs`. Ce sont les sorties standard et erreur des binaires multimédias (FFmpeg, mkvmerge, etc.) associées à une tâche précise et consultables dans l'onglet *Pipeline* de la tâche.
- **Journaux système et nœuds (objet de cette architecture)** : événements de cycle de vie de l'application, détection du matériel et sondes GPU, santé du cluster, chargement des plugins, erreurs d'authentification et exceptions non gérées.

---

## 2. Décisions d'Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Microsoft.Extensions.Logging                    │
│   (NodeHeartbeatService, ToolCapabilityProbe, ClusterMesh, Plugins)     │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                        SystemLogProvider                               │
│  - Masquage des secrets (tokens Bearer, mots de passe Plex, clés)      │
│  - Filtrage configurable du bruit (requêtes HTTP 200, pings de santé)  │
└───────────────────┬────────────────────────────────┬───────────────────┘
                    │                                │
                    ▼                                ▼ (Warning & Error)
┌──────────────────────────────────────┐  ┌──────────────────────────────┐
│  Tampon Circulaire en Mémoire        │  │  Table SQLite locale         │
│  (SystemLogRingBuffer)               │  │  (system_logs)               │
│  - 2 000 événements récents          │  │  - Rétention bornée (7 j)    │
│  - Zéro I/O disque, lecture instant  │  │  - Strictement non réconcilié│
└───────────────────┬──────────────────┘  └──────────────┬───────────────┘
                    │                                    │
                    ▼                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                     API Endpoints (/api/v1/system/logs)                │
│  - GET /api/v1/system/logs        (historique filtré et paginé)        │
│  - GET /api/v1/system/logs/stream (flux SSE temps réel)                │
│  - Outil MCP get_system_logs      (diagnostic autonome par agents IA)  │
└───────────────────┬────────────────────────────────────────────────────┘
                    │
                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                   Interface Angular (Console de logs)                  │
│  - Filtrage sévérité (Info, Warn, Error, Critical)                     │
│  - Recherche textuelle, auto-scroll, pause, export TXT / JSON          │
└────────────────────────────────────────────────────────────────────────┘
```

---

### A. Le Tampon Circulaire en Mémoire (`SystemLogRingBuffer`)

Pour garantir un impact nul sur les performances et éviter toute contention d'écriture sur le disque :
1. Un tampon circulaire thread-safe conserve les **2 000 derniers événements** de log en mémoire vive.
2. Chaque entrée `SystemLogEntry` est immuable et comprend :
   - `Id` : identifiant séquentiel ou GUID,
   - `Timestamp` : horodatage UTC précis (`DateTimeOffset`),
   - `Level` : `LogLevel` (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`),
   - `Category` : catégorie du composant émetteur (ex. `Encoding.NodeAgent.Services.ToolCapabilityProbe`),
   - `Message` : message de log formaté,
   - `Exception` : type, message et trace de pile formatée (le cas échéant),
   - `NodeId` et `NodeName` : identité du nœud émetteur,
   - `EventId` : identifiant d'événement d'infrastructure.
3. Les abonnés temps réel sont notifiés par un diffuseur asynchrone non-bloquant (`Channel<SystemLogEntry>` ou `SystemLogStreamBroadcaster`), garantissant que la lenteur d'un client HTTP ne ralentit jamais l'émission des logs.

---

### B. Le Fournisseur ASP.NET Core (`SystemLogProvider`) et Masquage

1. Une implémentation d'`ILoggerProvider` est enregistrée dans le conteneur d'injection de dépendances de l'API serveur et de l'agent worker.
2. **Masquage strict des données sensibles (`SystemLogSanitizer`)** :
   - Tous les jetons API Bearer (`enc_pat_...`), secrets d'enrôlement de conteneur, mots de passe de serveurs multimédias (Plex, Sonarr, Radarr) et clés privées sont automatiquement détectés et remplacés par `***REDACTED***`.
3. **Filtre anti-pollution par défaut** :
   - Les requêtes HTTP Kestrel de routine réussies (`Microsoft.AspNetCore.Hosting.Diagnostics`, `200 OK`), les vérifications périodiques d'infrastructure (`HealthChecks`) et les logs de transport Raft de niveau `Trace`/`Debug` sont exclus du tampon par défaut, mais activables à la demande en niveau `Debug`.

---

### C. Persistance Locale Bornée pour les Avertissements et Erreurs

1. Pour conserver l'historique des incidents au-delà d'un redémarrage du conteneur, les événements de sévérité égale ou supérieure à `Warning` (`Warning`, `Error`, `Critical`) sont enregistrés de façon asynchrone dans une table SQLite locale `system_logs`.
2. **Règle absolue d'isolation** :
   - Cette table est **strictement locale à l'hôte** et n'est **jamais réconciliée** par le protocole Raft ou le maillage [0049](0049-multi-server-shared-cluster.md), afin de préserver la bande passante inter-nœuds.
3. **Rétention bornée** :
   - Un mécanisme de nettoyage automatique glissant limite la table à un maximum de 5 000 lignes ou 7 jours d'historique.

---

### D. Endpoints API REST & Streaming SSE

Les endpoints sont regroupés sous `/api/v1/system/logs` et exigent une session authentifiée ou un jeton API avec le rôle `admin` :

1. `GET /api/v1/system/logs` :
   - **Paramètres** :
     - `minLevel` : filtre par sévérité minimale (`Debug`, `Information` par défaut, `Warning`, `Error`).
     - `category` : préfixe de catégorie (ex. `Encoding.Plugins`, `Encoding.NodeAgent`).
     - `search` : recherche textuelle insensible à la casse dans les messages et exceptions.
     - `nodeId` : identifiant du nœud ciblé (en cluster multi-nœuds, le serveur relaie la requête au nœud distant via le canal inter-nœuds).
     - `limit` : nombre maximum d'entrées retournées (défaut : 100, maximum : 1 000).
     - `since` : horodatage de coupure pour rafraîchissement incrémental.
   - **Retour** : liste ordonnée antichronologique d'objets `SystemLogEntryDto`.

2. `GET /api/v1/system/logs/stream` :
   - **Protocole** : Server-Sent Events (`text/event-stream`), réutilisant le modèle architectural d'`ExecutionStreamBroadcaster`.
   - **Paramètres** : mêmes filtres que la consultation REST (`minLevel`, `category`, `search`).
   - **Fonctionnement** : émet immédiatement les N derniers logs correspondants du tampon, puis pousse chaque nouvel événement en temps réel.
   - **Heartbeat** : émet un commentaire ou événement vide toutes les 15 secondes pour maintenir la connexion active à travers les proxys inverses (Nginx, Traefik).

3. `GET /api/v1/system/logs/categories` :
   - Retourne la liste des catégories distinctes présentes dans le tampon pour alimenter les listes déroulantes de l'interface.

---

### E. Intégration Serveur MCP pour les Agents IA

Conformément à [0061](0061-model-context-protocol-server.md) et [0065](0065-ai-media-tools-and-mcp-integration.md) :
1. Un outil MCP `get_system_logs` est exposé :
   - Arguments : `min_level` (ex. "Warning"), `category`, `search`, `limit`, `node_id`.
2. Permet aux agents IA d'auto-analyser les erreurs (ex. échec d'encodage matériel, délai d'attente d'un worker, erreur de connexion Plex) sans exiger de l'utilisateur qu'il inspecte les conteneurs manuellement.

---

### F. Interface Utilisateur Angular (Console de Logs Système)

L'interface web Angular intègre un visualiseur de logs moderne dans l'administration (ex. menu **Paramètres > Logs système** ou onglet **Logs** dans le détail d'un nœud) respectant scrupuleusement `UI_RULES.md` et les jetons de design [0035](0035-ui-design-tokens-theme-and-cards.md) :

1. **Barre de commandes supérieure** :
   - Filtre rapide par sévérité (boutons segmentés ou chips Material : *Tous*, *Infos*, *Avertissements*, *Erreurs*).
   - Sélecteur de catégorie avec autocomplétion.
   - Champ de recherche textuelle avec surlignage des occurrences trouvées.
   - Bouton de bascule **Pause / Reprendre** : en pause, un compteur affiche le nombre d'événements reçus en attente.
   - Boutons d'action : *Effacer la vue*, *Copier la sélection*, *Télécharger (TXT / JSON)*.
2. **Zone de visualisation console** :
   - Style terminal sombre avec typographie monospace lisible.
   - Badges colorés discrets pour les niveaux (`info`, `warn`, `error`, `crit`).
   - Horodatage local et UTC au survol.
   - Affichage pliable des traces de pile d'exceptions (`StackTrace`) pour ne pas encombrer la lecture.
   - **Défilement automatique intelligent** : la console suit les nouveaux logs tant que l'utilisateur est en bas de page ; dès qu'il remonte pour lire un message antérieur, le défilement se fige sans interrompre la réception.

---

## 3. Plan de Mise en Œuvre

### Phase 1 : Cœur Backend & Capture des Logs
- [ ] Créer les modèles `SystemLogEntry` et `SystemLogLevel` dans `Encoding.Contracts.Logging` ou `Encoding.Core.Logging`.
- [ ] Implémenter le service de masquage des secrets `SystemLogSanitizer`.
- [ ] Implémenter le tampon circulaire thread-safe `SystemLogRingBuffer` (2 000 entrées) avec notification asynchrone.
- [ ] Créer le fournisseur `SystemLogProvider : ILoggerProvider` et le brancher dans `Encoding.Api` et `Encoding.NodeAgent`.
- [ ] Ajouter les tests unitaires de masquage, de débordement du tampon et de filtrage.

### Phase 2 : Persistance SQLite Bornée & Endpoints API
- [ ] Créer la table SQLite locale `system_logs` et son repository de persistance asynchrone pour les niveaux `Warning` et supérieurs avec purge glissante.
- [ ] Créer les endpoints `GET /api/v1/system/logs` et `GET /api/v1/system/logs/categories`.
- [ ] Créer l'endpoint SSE `GET /api/v1/system/logs/stream` avec gestion du heartbeat et de l'annulation propre.
- [ ] Sécuriser les routes avec `RequireAuthorization(ClusterRole.Admin)`.
- [ ] Écrire les tests d'intégration d'API.

### Phase 3 : Intégration MCP & Support Multi-Nœuds
- [ ] Enregistrer l'outil `get_system_logs` dans le catalogue du serveur MCP interne (`McpToolsCatalog`).
- [ ] Ajouter le relai de logs inter-nœuds pour permettre au serveur d'interroger les logs d'un worker distant.
- [ ] Valider l'introspection autonome par les agents IA.

### Phase 4 : Interface Utilisateur Angular
- [ ] Créer le service Angular `SystemLogsService` gérant la connexion SSE et le tampon d'affichage local.
- [ ] Concevoir le composant `SystemLogViewerComponent` conforme à `UI_RULES.md` (barre de filtres, console sombre, badges, auto-scroll intelligent, pause/reprise, export).
- [ ] Intégrer le composant dans la navigation d'administration (vue système générale et onglet par nœud).
- [ ] Valider avec les tests unitaires Angular et le build de production.
