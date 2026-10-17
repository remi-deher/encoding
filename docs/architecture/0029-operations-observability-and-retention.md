# 0029 — Exploitation : santé, métriques, journaux, rétention et migrations

## Décision

Chaque serveur expose son état de la même façon, sans nœud privilégié.

- **Santé.** `/health/live` ne dépend d’aucun service (le processus répond). `/health/ready` vérifie la base SQLite locale et reste le critère de retrait d’un serveur d’un répartiteur. `/health/details` exécute tous les contrôles : base, migrations, cluster (workers en ligne, baux expirés depuis plus de 2 minutes), file de sauvegardes de flows, accès en écriture et espace libre des volumes d’artefacts et de sauvegardes. `healthy` et `degraded` répondent 200, `unhealthy` répond 503. Les chemins du serveur ne sont jamais renvoyés.
- **Métriques.** `/metrics` produit le format texte Prometheus. L’état de cluster visible par un serveur est lu dans sa base SQLite locale et peut différer de celui des pairs pendant la convergence du maillage ; chaque serveur publie ses propres métriques. Les mesures du processus (CPU, mémoire, GC, requêtes HTTP par route, rétention) sont aussi locales. L’endpoint est ouvert par défaut ; `Operations:Metrics:Token` exige un jeton Bearer et `Operations:Metrics:Enabled=false` le coupe.
- **Journaux.** Les journaux passent par `Microsoft.Extensions.Logging`. Les images Docker activent le formateur console `json` (une ligne par entrée, horodatage UTC, portées incluses). Chaque requête reçoit un identifiant `X-Correlation-Id` (celui du client s’il est valide, sinon généré), renvoyé dans la réponse et présent dans la portée des journaux. Le journal de requête contient la méthode, la route, le statut et la durée, jamais la chaîne de requête ni les en-têtes.
- **Rétention des artefacts.** Désactivée par défaut (`Operations:ArtifactRetention:Enabled`). Activée, elle supprime le dossier `<executionId>` des exécutions réussies ou annulées terminées depuis `Days` jours, et des exécutions échouées après `FailedDays` jours (0 les conserve). Sont toujours épargnées : les exécutions en cours, celles dont la publication demandée n’est pas confirmée, et celles qui détiennent encore une réservation de média. L'état local et `execution_work.artifacts_purged_at` rendent le traitement idempotent et observable ; la convergence réplique ensuite les changements prévus. Les fichiers `.tmp` orphelins plus vieux que `TempFileHours` sont supprimés dans le même passage. Le mode `DryRun` journalise et compte sans rien supprimer. Les fichiers publiés dans les bibliothèques (`.encoding-results/`) ne sont jamais touchés.
- **Migrations.** Les fichiers suivent `NNNN_description.sql` avec un numéro unique ; le chargement échoue sinon. Chaque migration enregistre l’empreinte SHA-256 de son contenu (fins de ligne normalisées) dans `schema_migrations.checksum`. Une migration déjà appliquée dont le contenu change bloque le démarrage : corriger par une nouvelle migration. Les lignes sans empreinte (bases antérieures) sont complétées au premier démarrage. Une version enregistrée mais absente du binaire (nœud plus ancien que le schéma) est signalée `degraded`, sans bloquer.

## Doublon 0006

Deux fichiers portaient le numéro 0006. `0006_library_node_access_activation.sql` est renommé `0023_library_node_access_activation.sql` sans changer son contenu. `MigrationCatalog.RenamedFrom` relie les deux noms : une base qui a déjà enregistré l’ancien nom considère la migration comme appliquée et ne rejoue pas son SQL ; une base neuve l’exécute à sa nouvelle position. L’état final du schéma est le même dans les deux cas.

## Limites

- `/metrics` et `/health/details` n’ont pas d’authentification utilisateur ; les limiter au réseau d’exploitation tant que l’authentification de l’API n’est pas en place.
- Les compteurs HTTP et de rétention sont par processus et repartent de zéro au redémarrage.
- Les artefacts restent soumis à leur configuration de stockage et ne sont pas rendus disponibles par le maillage SQLite. Sur un volume non partagé, chaque serveur ne voit que ses fichiers.
- L’agent worker n’expose pas encore ses propres métriques.
