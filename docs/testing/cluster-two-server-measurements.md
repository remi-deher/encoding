# Mesure de coordination à deux serveurs

Exécution locale du test `ClusterTaskTransferTests.MeasuresTwoServerOwnershipLoad`, avec `ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS=1` : 7 000 tâches, deux SQLite distincts, deux serveurs logiques **dans un même processus**, deux workers synthétiques, claims/renouvellements et lectures paginées en concurrence avec capture incrémentale et réplication.

| Mesure | Résultat local antérieur à la dernière modification |
|---|---:|
| Mise en place | 4 279 ms |
| Exercice | 13 460 ms |
| Claims p95 / p99 | 397 / 505 ms |
| Renouvellements p95 / p99 | 79 / 92 ms |
| Vue générale p95 / historique p95 | 231 / 185 ms |
| Admissions écrivain A / B | 281 / 286 |
| Rejets / expirations écrivain | 0 / 0 |

Cette mesure n’est ni un SLO ni une validation sur deux machines physiques, NAS, coupures réseau réelles ou 7 000 encodages. Une nouvelle exécution sur le commit final et son artefact brut restent à produire. Le scénario média distinct teste réellement encodage FFmpeg, transfert, remux et publication sur fichiers synthétiques.

Commande :

```sh
ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS=1 dotnet test backend/tests/Encoding.Runtime.Tests -c Release --no-build --filter FullyQualifiedName~MeasuresTwoServerOwnershipLoad --logger 'console;verbosity=detailed'
```
