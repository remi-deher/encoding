# 0048 — Bloc « Bibliothèque sur ce nœud »

## Contexte

Chaque nœud déclare son accès à chaque bibliothèque (accès direct, transfert, non pris en charge). Un flow n'avait aucun moyen de brancher selon cet accès : un remux ou une modification d'en-tête MKV doit rester sur le nœud qui monte la bibliothèque, alors qu'un encodage peut être confié à un worker plus puissant.

## Décision

Le plugin intégré `nodes` passe en version 1.2.0 et ajoute le composant `nodes-library-local` (condition, sorties **Oui** / **Non**, aucune configuration).

- **Oui** : le worker courant est le nœud d'accès à la bibliothèque du fichier (`source == worker`, accès direct).
- **Non** : le worker n'atteint le fichier que par transfert.

La décision est déterministe et locale : elle compare `lease.SourceNodeId` au worker, sans appel externe. Le simulateur la calcule de la même façon à partir du profil de test (nœud source et worker courant), sans hypothèse à saisir.

Le délégué n'est pas un nouveau bloc : la sortie **Non** se relie au relais existant `nodes-handoff` (0021), éventuellement précédé de `nodes-require-capability`. Exemple :

```
Convertir en H.265 ← Bibliothèque sur ce nœud ?
  Oui → encode sur place
  Non → nodes-handoff (worker capable) → encode
```

Pour un remux réservé au nœud d'accès, la sortie **Non** mène à un bloc d'échec (`base-complete-flow` en échec).

## Éligibilité

Le répartiteur évalue les capacités requises par toutes les branches possibles d'un segment. Sans élagage, un nœud qui monte la bibliothèque mais n'a pas l'encodeur serait refusé pour tout le segment alors qu'il ne suivra que la branche Non. `RuntimeHandoffPolicy.Segment` et `RuntimeEligibility.CanRun` acceptent donc `libraryLocal` :

- inconnu (pré-filtre avant la résolution de l'accès) : le worker doit satisfaire au moins l'une des deux branches ;
- connu (après lecture de l'accès, `source == worker`) : seule la branche réellement suivie est vérifiée.

La validation de publication et le calcul des contraintes `nodes-run-on-node` considèrent toujours les deux branches.

## Compatibilité

- Les références existantes (plugin `nodes` 1.0.0 ou 1.1.0) restent résolubles (`compatiblePluginVersions`).
- Un agent qui annonce `nodes` 1.1.0 ne peut pas réclamer un segment contenant le nouveau bloc ; l'agent annonce désormais 1.2.0.
- Pendant une mise à jour progressive, un flow qui utilise le bloc n'est attribué qu'aux nœuds déjà mis à jour.

## Limites

- Le bloc répond à « ce worker a-t-il l'accès direct ? », pas à « quels nœuds ont la bibliothèque ? ».
- Le verrou fichier local des médias (`SafeMediaPaths.Lock`) n'est pas modifié ici.
- La réservation du média en base reste la seule garantie d'exclusivité entre nœuds.

## Vérification

`ContractTests.LibraryLocalBlockPrunesBranchesPerWorkerAccess` couvre : segments avec branche connue ou inconnue, éligibilité d'un worker sans encodeur sur la branche Non, d'un détenteur de bibliothèque avec ou sans encodeur, ancien agent, références 1.1.0 et simulation.
Non couvert automatiquement : exécution réelle sur deux nœuds (tests PostgreSQL ignorés sans base) et exécution du runner de bout en bout avec le nouveau bloc.
