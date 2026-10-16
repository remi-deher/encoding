# Prototype de consensus Raft à trois nœuds

Ce scénario lance trois processus .NET indépendants avec `DotNext.Net.Cluster` 6.10.0, un Write-Ahead Log et un dossier local distinct pour chaque nœud. Il vérifie l’élection, la validation d’une entrée au quorum, le refus d’écriture après perte du quorum, une nouvelle élection, le retour d’un pair et le rattrapage du journal. Il arrête ensuite les trois processus, redémarre un seul nœud sans pairs joignables et vérifie que son index validé est lu depuis son journal local avant de redémarrer le cluster.

```sh
dotnet run --project backend/tools/ClusterConsensusPrototype -- test
```

Le test utilise uniquement `127.0.0.1`, des ports fixes et des dossiers temporaires nettoyés à la fin. Il demande .NET 10 et un accès NuGet pour restaurer `DotNext.Net.Cluster`.

## Portée

Ce test valide la persistance du journal de consensus et les décisions de quorum de la bibliothèque. Il emploie une configuration statique préchargée en mémoire pour les trois membres et une state machine sans projection métier. Il ne valide donc pas la persistance du membership, l’application des commandes au modèle Encoding, les sessions/idempotence client ou la sécurité du transport. Le transport TCP reste lié à loopback dans ce banc d’essai; toute exposition réseau nécessite une identité de pair et une protection cryptographique adaptées.
