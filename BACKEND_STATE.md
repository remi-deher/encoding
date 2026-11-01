# État du backend et pistes d'amélioration

Bilan de l'audit de séparation des responsabilités et des corrections appliquées sur la branche `cez/cff025a5` (fork de `cez/ac1b8bc0`). Les chiffres viennent de mesures sur `backend/src` (grep, `wc`) et de suites de tests locales ; rien ici n'a été exercé en conditions réelles sur média de production.

## 1. État actuel

### Structure
- Cinq projets : `Contracts` → `Core` → `Api` et `NodeAgent` (+ `Launcher`). Les références sont correctement en couches et sont vérifiées par `ArchitectureTests`.
- Interfaces aux frontières utiles : `IProcessToolRunner`, `ILocalPublicationArtifactStore` (+ `TimeProvider` sur le store). Les services MCP/IA ne sont plus un seul monolithe partiel.
- Aucun fichier source de plus de 600 lignes.
- Lignes de code de plus de 200 caractères plafonnées par un test (cliquet).

### Corrigé dans cette branche
| Point | Correction | Garde-fou |
|---|---|---|
| Aucune vérification des couches | `ArchitectureTests` (références de projets, usings) | test |
| Stockage dans les endpoints | `UniqueConflict.GuardAsync` ; SQL de `ClusterTaskEndpoints` déplacé dans `ClusterTaskPeerHandler` | test |
| Accès fichier dans les endpoints | `FileResults` | test |
| `RuntimeFlowRunner.RunAsync` (~950 lignes) | `RuntimeFlowExecution` + `FlowExecutionState` / `StepOutcome` | tests existants |
| Dix fichiers de 600 à 1650 lignes | Scindés ; extraction réelle MCP/IA (handlers + services) | test (plafond 600) |
| Lignes de plus de 200 caractères | Coupées ; reste plafonné | test (cliquet) |
| Tests instables sous charge | Collection `sqlite` partagée, course `MediaRelayAsync`, reset `artifactDownloads`, CTS 90 s | 10× verts |
| SQL filtre libre dans dépôts | Constantes / concat schema-only + cliquet | test |
| Catch vides worker/publish | Log Debug/Warning | audit |
| `.Result` / sync-over-async | Audit : ADO sync conservé ; faux positifs documentés | — |
| Endpoints → dépôts / `new HttpClient()` | Cliquets `EndpointRepositoryDebt` (29) et `HttpClientConstructionDebt` (3) | test |
| Règles et décision non écrites | `BACKEND_RULES.md` §3, ADR 0031 ; fiche `runtime.md` (organisation + mermaid claim→publish) | — |

### Vérification
- Compilation sans avertissement (dernière preuve locale).
- Suite `Encoding.Runtime.Tests` (`Category!=Stress`) après merge Phases 3+4 : **615 réussis, 17 ignorés, 0 échec**, ~5 m.
- Phase 1 : **10 runs complets verts** (612/17 à l’époque).
- Le refactor runtime n’est couvert que par la suite existante, pas par un scénario bout en bout sur média réel.
- Les tests de `backend/tests` n’ont pas été reformatés.

## 2. À corriger (dette réelle)

1. **Tests instables sous charge** : **fait (Phase 1)**.
2. **Découpage cosmétique vs réel** : **fait pour MCP/IA (Phase 3)** — handlers domaine + registre ; services Conversations/Send/Actions/Support. **Reste** : `RuntimeRepository`, `ClusterEntityReconciliation`, `MediaActions` encore monolithiques ; 29 endpoints injectent encore un dépôt (`EndpointRepositoryDebt`).
3. **`RuntimeFlowExecution`** : **fait (Phase 4)** — `FlowExecutionState` + `StepOutcome` + applicateur unique ; bail derrière méthodes thread-safe.
4. **SQL à la main dans les dépôts** : **fait (Phase 2)** sur les filtres libres ciblés. Les `Sql.Command` longs restent (hors scope).
5. **Gestion d'erreurs large** : **fait (Phase 2)** pour les catch silencieux critiques listés. Catch acceptés : heartbeats / claim multi-URL, sondes matérielles, `ClusterReconciliationService`.
6. **Appels bloquants** : **fait (Phase 2, audit)**.
7. **Contrats / schémas** : états et sorties encore trop souvent en chaînes libres ; mapping MCP ↔ assistant à ancrer ; OpenAPI runtime partiel.
8. **Cliquets maintenance** : longueur/complexité de méthode, plafonds centralisés, format CI reporté.
9. **Documentation** : fiche `runtime` à jour (Phase 4) ; vérifier `open-questions.md` pour le reliquat.

## 3. Pistes d'amélioration

### Séparation et testabilité
- Poursuivre les interfaces aux frontières qui comptent (dépôts consommés par services, pas partout).
- Extraire des services minces pour réduire `EndpointRepositoryDebt` (29 fichiers) — prochain ratchet naturel.
- Sortir le SQL de `ClusterEntityReconciliation` et `RuntimeRepository` dans des classes de requêtes par agrégat.
- Migrer `WorkerEnrollmentService` / `WorkerClusterDiscovery` / `McpCommand` vers `IHttpClientFactory` (liste `HttpClientConstructionDebt`).

### Contrats
- Types/constantes nommées pour états d’exécution et sorties de blocs ; helpers de parsing.
- Une source de vérité `BusinessOperationCatalog` + mapping MCP/assistant testé.
- Enrichir OpenAPI sur le sous-ensemble runtime/exécutions (`$ref` minimaux).
- Tests de compatibilité des messages déjà versionnés (`ClusterTaskEvent.Version`, `ProtocolVersion`).
- Types forts pour les identifiants (`ExecutionId`, `NodeId`) — piste ultérieure.

### Maintenance
- Cliquet longueur/complexité de méthode avec baseline ; centraliser les plafonds d’architecture.
- `.editorconfig` en suggestion ; `dotnet format --verify-no-changes` seulement après un commit de format dédié (hors campagne).
- Journaux structurés avec corrélation (exécution, nœud, bail) sans secret.

### Clarté
- README court par dossier de `Encoding.Api` si besoin.
- Réduire les noms trop proches (composant de flow, composant UI, nœud de cluster) conformément à `AGENTS.md`.

## 4. Ordre suggéré (campagne)
1. ~~Stabiliser les tests instables.~~ **fait**
2. ~~Auditer SQL dynamique, `catch (Exception)` et `.Result`.~~ **fait**
3. ~~Interfaces aux frontières + extraction `McpService` / `AiAssistantService`.~~ **fait**
4. ~~État explicite pour `RuntimeFlowExecution`.~~ **fait**
5. Contrats versionnés et génération/alignement de schémas.
6. Analyseurs et cliquets de complexité en CI.
