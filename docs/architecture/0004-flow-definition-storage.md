# 0004 — Format et sauvegarde des définitions de flow

## Statut

Implémenté pour les versions validées ; voir 0014-flow-version-storage-and-backups.md pour le stockage, la file de sauvegardes et les limites.

## Décision

- Une définition de flow est un document JSON versionné, indépendant de `ngx-vflow` et des détails du canevas Angular.
- SQLite locale est le stockage de chaque serveur ; les définitions sont réconciliées entre pairs selon 0049. Le document est conservé en JSON et chaque version publiée est immuable ; les modifications créent une nouvelle version.
- Le document décrit les métadonnées du flow, les identifiants et versions des plugins/composants, les configurations de nœuds et les connexions entre sorties et entrées typées. Les positions du canevas peuvent être conservées comme métadonnées de présentation et ne changent pas la sémantique d’exécution.
- Une sauvegarde JSON distincte est conservée pour chaque version. Les fichiers sont écrits de façon atomique dans un dossier persistant configurable, séparé de SQLite. Chaque serveur régénère localement les sauvegardes après réception des définitions réconciliées ; chaque dossier doit être sauvegardé indépendamment.
- Les fichiers de sauvegarde sont des instantanés récupérables, pas une deuxième autorité d’écriture. Une restauration doit valider le document et sa version avant de recréer une définition en base. Les versions plus récentes en base ne sont pas modifiées par la restauration d’un ancien instantané sans action explicite.

## Conséquences

- Le contrat JSON doit inclure un numéro de format distinct de la version métier du flow, pour permettre de faire évoluer le schéma sans confondre les versions d’exécution.
- L’API valide les références de composants, les configurations, les sorties/entrées et les invariants du graphe avant publication. L’éditeur ne constitue pas la frontière de validation.
- L’écriture en base et celle dans le système de fichiers ne forment pas une transaction atomique. L’implémentation doit rendre les sauvegardes rejouables, détecter les instantanés absents et exposer un état de sauvegarde ; un succès d’enregistrement ne doit pas laisser croire qu’une copie de secours existe si son écriture a échoué.
- Les chemins de sauvegarde sont configurés par l’hôte et montés en volume. Aucun chemin propre à une machine ne fait partie de la définition de flow.

## À préciser avant l’implémentation complète

- Le chemin par défaut et la politique de rétention des snapshots.
- La fréquence de vérification et le parcours de restauration lorsqu’une sauvegarde manque ou que la base est indisponible.
- Les composants métier additionnels souhaités dans le catalogue. Les composants génériques Action, Condition, Variable et Boucle (avec sous-graphe d’itération et sortie de fin) sont déjà prévus dans `AGENTS.md`.
