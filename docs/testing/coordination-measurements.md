# Mesurer la coordination SQLite

Cette sonde utilise les méthodes réelles `RuntimeRepository.ClaimAsync`, `RenewAsync` et `ClusterEntityReconciliation.CaptureLocalChangesAsync`, avec les migrations courantes et le fournisseur SQLite du backend. Elle ne lance aucun outil média et ne touche aucun fichier utilisateur.

## Reproduire

```sh
ENCODING_TEST_COORDINATION_MEASUREMENTS=1 dotnet test \
  backend/tests/Encoding.Runtime.Tests --configuration Release \
  --filter FullyQualifiedName~CoordinationMeasurementTests \
  --logger 'console;verbosity=detailed'
```

Les lignes `COORDINATION_MEASUREMENT` contiennent les mesures JSON. Un logger TRX conserve aussi ces sorties. Sans cette variable, la sonde est explicitement ignorée : les temps ne sont pas des assertions de performance en CI. Les tests de régression `LoadContentionTests` et le workflow nocturne `stress.yml` restent les garde-fous de charge existants ; cette sonde les complète pour les claims, renouvellements et captures.

Chaque cas utilise une nouvelle base temporaire : 128 travaux, huit clients concurrents sur un même nœud worker, 32 attributions et quatre renouvellements par attribution (128 au total). Les clients démarrent ensemble sur des threads dédiés, sans modifier les paramètres globaux du ThreadPool. Le second cas ajoute un client qui effectue 16 modifications de métadonnées suivies de captures incrémentales. Présence, capacités et accès sont valides ; aucun traitement média n'intervient.

Avant chaque charge, la capture initiale est terminée puis huit captures au repos sont mesurées. Elles doivent produire zéro événement et zéro admission d'écriture. La charge vérifie les 32 identités distinctes attribuées, le succès de tous les renouvellements et l'absence de refus/expiration de la file. Aucun seuil temporel précis n'est imposé.

## Observation locale du 2026-10-23

SDK .NET 10.0.402, runtime 10.0.13, compilation Release. File d'écriture complète activée, capture incrémentale et index courants. Trois passages par cas ; valeurs du tableau = médiane des mesures de chaque passage, et non percentile recalculé sur les échantillons réunis.

| Mesure | Sans capture concurrente | Avec capture concurrente |
| --- | ---: | ---: |
| Durée de la charge | 1 630,093 ms | 1 840,111 ms |
| Claim p95 | 353,864 ms | 353,544 ms |
| Claim p99 | 397,667 ms | 412,077 ms |
| Renouvellement p95 | 77,179 ms | 79,643 ms |
| Renouvellement p99 | 87,485 ms | 97,646 ms |
| Attente cumulée de la file | 5 693,996 ms | 7 494,769 ms |
| Détention cumulée de l'écrivain | 1 521,049 ms | 1 623,150 ms |
| Huit captures au repos | 35,669 ms | 30,678 ms |

Les six passages ont zéro refus, expiration et détention supérieure au seuil de 1 s. Tous les renouvellements réussissent, aucune attribution n'est dupliquée et les captures au repos n'admettent aucun écrivain. Les attentes cumulées peuvent dépasser le temps total puisque plusieurs clients attendent simultanément. Les [résultats JSON](coordination-measurements-2026-10-23.json) conservent les six observations, leurs effectifs et leurs maxima.

Sur ce petit scénario local, la capture concurrente ajoute du travail à la file mais ne reproduit aucune perte de bail. Avec 32 claims par passage, leur p99 est simplement le maximum ; ces petits échantillons ne caractérisent pas une queue de distribution de production. Cette sonde mesure les dépôts directement, sans HTTP, encodage, transfert de médias, stockage NAS ou réplication entre hôtes. Elle ne compare pas à une ancienne version et ne permet pas d'attribuer un facteur de gain aux correctifs précédents. Elle ne justifie pas un changement de moteur SQL.

## Compléter sur un environnement représentatif

Relever par intervalle `encoding_sqlite_writer_*` (attente, détention, refus, expirations), les latences des routes claim/renew, le retard des vecteurs de réplication et l'occupation des volumes. Garder les mêmes données, accès et concurrence entre essais. Les compteurs cumulés donnent des moyennes par intervalle, pas des p95/p99 ; ceux-ci doivent provenir des observations individuelles ou du collecteur HTTP. Une validation multi-hôtes ou de production reste distincte de ces résultats synthétiques.
