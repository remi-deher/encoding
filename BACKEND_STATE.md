# État du backend et pistes d'amélioration

Bilan de l'audit de séparation des responsabilités et des corrections appliquées sur la branche `cez/ac1b8bc0`. Les chiffres viennent de mesures sur `backend/src` (grep, `wc`) ; rien ici n'a été exercé en conditions réelles.

## 1. État actuel

### Structure
- Cinq projets : `Contracts` → `Core` → `Api` et `NodeAgent` (+ `Launcher`). Les références sont correctement en couches et sont maintenant vérifiées par `ArchitectureTests`.
- Environ 850 classes ou records pour 8 interfaces : très peu de points de substitution.
- Aucun fichier source de plus de 600 lignes (le plus gros : `AiClusterService`, 589).
- 227 lignes de code de plus de 200 caractères restent (littéraux SQL et textes), plafonnées par un test.

### Corrigé dans cette branche
| Point | Correction | Garde-fou |
|---|---|---|
| Aucune vérification des couches | `ArchitectureTests` (références de projets, usings) | test |
| Stockage dans les endpoints | `UniqueConflict.GuardAsync` ; SQL de `ClusterTaskEndpoints` déplacé dans `ClusterTaskPeerHandler` | test |
| Accès fichier dans les endpoints | `FileResults` | test |
| `RuntimeFlowRunner.RunAsync` (~950 lignes) | `RuntimeFlowExecution` : une méthode par famille d'étape | tests existants |
| Dix fichiers de 600 à 1650 lignes | Scindés en classes partielles par responsabilité | test (plafond 600) |
| Lignes de plus de 200 caractères | 666 → 227, changement d'espaces seul vérifié | test (cliquet) |
| Règles et décision non écrites | `BACKEND_RULES.md` §3, ADR 0031 | — |

### Vérification
- Compilation sans avertissement.
- Suite `Encoding.Runtime.Tests` (filtre `Category!=Stress`) : **10 runs complets verts d’affilée** après Phase 1 — 612 réussis, 17 ignorés, 0 échec, ~4m50–5m (`timeout 600 dotnet test backend/tests/Encoding.Runtime.Tests --no-build -m:1`). Stabilisation : collection `sqlite` partagée pour `FfmpegBuilderTests` / `SourceWriteBarrierTests`, course `MediaRelayAsync` corrigée, reset `artifactDownloads`, CTS encode réels à 90 s.
- Le refactor de `RuntimeFlowExecution` n'est couvert que par la suite existante, pas par un scénario bout en bout sur média réel.
- Les tests de `backend/tests` n'ont pas été reformatés.

## 2. À corriger (dette réelle)

1. **Tests instables sous charge** : ~~trois tests intermittents~~ **fait (Phase 1)** — 10× verts.
2. **Découpage cosmétique** : les classes partielles raccourcissent les fichiers mais gardent la même classe. `McpService`, `AiAssistantService`, `RuntimeRepository`, `ClusterEntityReconciliation`, `MediaActions` ont toujours les mêmes dépendances et le même état partagé.
3. **`RuntimeFlowExecution`** : les variables locales de l'ancienne méthode sont devenues des champs mutables (`next`, `current`, `context`, `terminal`…) partagés par toutes les étapes. L'état est implicite ; un objet d'état explicite et des étapes qui retournent un résultat seraient plus sûrs.
4. **SQL à la main dans les dépôts** : ~~interpolations `{filter}`/`{obsolete}`/`{table}=@id`~~ **fait (Phase 2)** sur `ResourceRepository`, `ExecutionRepository`, `ClusterTaskMaintenance`, `ClusterEntityReconciliation.Storage` — constantes / concat schema-only + cliquet `AuditedRepositoriesDoNotInterpolateFreeFormSqlFilters`. Les `Sql.Command` longs restent (hors scope).
5. **Gestion d'erreurs large** : ~~catch vides silencieux sur worker/publish/replace/cleanup~~ **fait (Phase 2)** — log Debug/Warning. **Catch critiques restants acceptés** : boucles heartbeat / claim multi-URL (déjà loguées au niveau supérieur), sondes matérielles / discovery (Debug), `ClusterReconciliationService` (déjà Warning). Compteur « 97 fichiers » abandonné au profit de cette liste.
6. **Appels bloquants** : **fait (Phase 2, audit)** — conserver `GetAwaiter().GetResult()` ADO.NET dans `SqliteDatabase` / `TransactionReader` (API sync `DbConnection`). `AiAssistantEndpoints.forwarded.Result` = propriété de réponse, pas `Task.Result`. `DeviceCodeAuthService` / `SqliteWriter` : lecture après `IsCompletedSuccessfully` uniquement.
7. **Documentation** : mettre à jour la fiche de domaine `runtime` (nouvelle organisation de l'exécution) et vérifier `open-questions.md`.

## 3. Pistes d'amélioration

### Séparation et testabilité
- Introduire des interfaces aux frontières qui comptent, pas partout : stockage de fichiers (`IArtifactStore`), exécution de processus, horloge (`TimeProvider`), dépôts utilisés par les services. Aujourd'hui les services dépendent de classes concrètes, donc les tests passent par une base réelle.
- Extraire de vrais services des gros ensembles : `McpService` (un gestionnaire par domaine d'outils, enregistrés dans un registre), `AiAssistantService` (actions, conversations, envoi, stockage), `RuntimeRepository` (claim, leases, steps).
- Sortir le SQL de `ClusterEntityReconciliation` et `RuntimeRepository` dans des classes de requêtes par agrégat.
- Étendre `ArchitectureTests` : endpoints qui n'appellent que des services, `Core`/`Contracts` sans dépendance ASP.NET, interdiction de `new HttpClient()` hors fabrique.

### Contrats
- Un fichier de contrats par domaine, avec des records immuables et des valeurs d'énumération nommées au lieu de chaînes libres (états, sorties de blocs).
- Versionner explicitement les messages de maillage et de runtime ; des tests de compatibilité entre versions mixtes (déjà une exigence du cluster).
- Générer ou valider la spécification OpenAPI et le schéma des outils MCP à partir des mêmes types, pour éviter la dérive entre `BusinessOperationCatalog`, `McpService` et les endpoints.
- Types forts pour les identifiants (`ExecutionId`, `NodeId`) afin d'éviter les inversions de `Guid`.

### Maintenance
- Cibler les 227 lignes restantes : constantes de requêtes SQL, fichiers de ressources pour les prompts.
- Réduire la taille des méthodes : ajouter un test ou un analyseur (complexité cyclomatique, longueur de méthode) avec un cliquet comme pour les fichiers.
- Activer les analyseurs Roslyn / `.editorconfig` en erreur pour le style, et rendre `dotnet format` vérifiable en CI (le contrôle actuel signale déjà un très grand nombre de différences : à régler une fois pour toutes).
- Dériver les plafonds d'architecture d'une seule configuration plutôt que de constantes dans le test.
- Journaux structurés avec identifiants de corrélation (exécution, nœud, bail) sans secret.

### Clarté
- Un `README` court par dossier de `Encoding.Api` (rôle, ce qui y entre, ce qui n'y entre pas).
- Nommer les classes partielles par responsabilité et garder un en-tête de rôle dans chaque fichier.
- Diagramme de flux d'une tâche, du claim à la publication, dans la fiche `runtime`.
- Réduire les noms trop proches (composant de flow, composant UI, nœud de cluster) conformément à `AGENTS.md`.

## 4. Ordre suggéré
1. Stabiliser les tests instables.
2. Auditer SQL dynamique, `catch (Exception)` et `.Result`.
3. Interfaces aux frontières (fichiers, processus, horloge), puis services extraits de `McpService` et `AiAssistantService`.
4. État explicite pour `RuntimeFlowExecution`.
5. Contrats versionnés et génération de schémas.
6. Analyseurs et cliquets de complexité en CI.
