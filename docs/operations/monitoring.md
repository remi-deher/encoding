# Supervision

Voir [0029](../architecture/0029-operations-observability-and-retention.md) pour les décisions.

## Endpoints

| Endpoint | Usage |
| --- | --- |
| `GET /health/live` | Vivacité du processus (sonde Docker/orchestrateur). |
| `GET /health/ready` | PostgreSQL joignable (retrait du répartiteur). |
| `GET /health/details` | Tous les contrôles, avec données chiffrées. Statut global `healthy`, `degraded` ou `unhealthy`. |
| `GET /metrics` | Métriques Prometheus. |

Contrôles de `/health/details` : `database`, `migrations`, `cluster`, `flow-backups`, `artifact-storage`, `backup-storage`.

## Prometheus

```yaml
scrape_configs:
  - job_name: encoding
    metrics_path: /metrics
    authorization:
      credentials: <valeur de Operations__Metrics__Token>
    static_configs:
      - targets: ['encoding-api-1:8080', 'encoding-api-2:8080']
```

Les métriques de cluster (`encoding_nodes`, `encoding_executions`, …) sont identiques sur chaque serveur : agréger avec `max by (…)` plutôt que `sum`.

| Métrique | Signification |
| --- | --- |
| `encoding_up`, `encoding_database_up` | Le serveur et PostgreSQL répondent. |
| `encoding_nodes{status}` | Nœuds en ligne / hors ligne (retirés exclus). |
| `encoding_executions{state}` | Exécutions par état. |
| `encoding_executions_oldest_queued_age_seconds` | Ancienneté de la plus ancienne exécution en file. |
| `encoding_execution_leases_expired` | Exécutions en cours dont le bail a expiré. |
| `encoding_media_reservations_active` | Médias réservés par un bail valide. |
| `encoding_flow_backup_jobs{status}`, `encoding_flow_backup_oldest_pending_age_seconds` | File de sauvegardes de flows. |
| `encoding_schema_migrations_applied`, `encoding_schema_migrations_pending` | Schéma. |
| `encoding_artifacts_pending_purge` | Exécutions terminées dont les artefacts sont encore sur disque. |
| `encoding_artifact_retention_*` | Passes, exécutions purgées, octets libérés, erreurs, dernière passe. |
| `encoding_http_requests_total{method,route,status}` | Requêtes par route. |
| `encoding_http_request_duration_seconds_total` | Durée cumulée (diviser par le compteur pour la moyenne). |
| `process_*`, `dotnet_*` | CPU, mémoire, GC, pool de threads. |

Alertes suggérées :

```yaml
- alert: EncodingDown
  expr: up{job="encoding"} == 0
  for: 2m
- alert: EncodingDatabaseDown
  expr: max(encoding_database_up) == 0
  for: 1m
- alert: EncodingNoWorker
  expr: max(encoding_nodes{status="online"}) == 0
  for: 10m
- alert: EncodingExpiredLeases
  expr: max(encoding_execution_leases_expired) > 0
  for: 5m
- alert: EncodingQueueStuck
  expr: max(encoding_executions_oldest_queued_age_seconds) > 3600 and max(encoding_nodes{status="online"}) > 0
  for: 15m
- alert: EncodingFlowBackupLate
  expr: max(encoding_flow_backup_oldest_pending_age_seconds) > 900 or max(encoding_flow_backup_jobs{status="failed"}) > 0
  for: 10m
- alert: EncodingRetentionFailing
  expr: increase(encoding_artifact_retention_errors_total[1h]) > 0
```

## Journaux

Les images activent `Logging__Console__FormatterName=json`. Pour un exécutable lancé hors Docker, définir les mêmes variables. Chercher une requête par son identifiant : le client peut envoyer `X-Correlation-Id`, la réponse le renvoie, et le champ `CorrelationId` figure dans la portée de chaque ligne JSON écrite pendant la requête.

## Rétention des artefacts

Activer explicitement, d’abord en simulation :

```yaml
Operations__ArtifactRetention__Enabled: "true"
Operations__ArtifactRetention__DryRun: "true"   # journalise sans supprimer
Operations__ArtifactRetention__Days: "30"       # réussies et annulées
Operations__ArtifactRetention__FailedDays: "0"  # 0 : conserver les échecs
Operations__ArtifactRetention__IntervalMinutes: "60"
Operations__ArtifactRetention__BatchSize: "50"
Operations__ArtifactRetention__TempFileHours: "24"
```

Rechercher dans les journaux « à supprimer (simulation) » et contrôler `encoding_artifacts_pending_purge`, puis retirer `DryRun`. Le résultat publié dans la bibliothèque n’est pas concerné. Une exécution purgée ne peut plus servir de source de relance ; une relance crée une nouvelle exécution qui retransfère le média.

## Seuils de santé

`Operations__Health__MinFreeDiskBytes` (5 Gio par défaut) et `Operations__Health__BackupLagMinutes` (15) règlent le passage à `degraded`.
