# 0018 — Surveillance et plafonds de traitement

## Hiérarchie

Trois plafonds indépendants doivent tous autoriser le démarrage :

1. Global du nœud : toutes ses bibliothèques confondues.
2. Global de la bibliothèque : tous ses nœuds confondus.
3. Couple nœud/bibliothèque : uniquement cette bibliothèque sur ce nœud.

Ces valeurs sont des maxima, jamais des places réservées ni des quotas de fichiers. Pour une bibliothèque plafonnée à cinq, A peut être plafonné à trois et B à deux ; leurs propres plafonds globaux peuvent encore réduire le nombre effectivement attribué. Une limite de bibliothèque ou de couple vide n’ajoute aucun plafond. Une limite globale de nœud vide conserve la capacité locale annoncée par son agent.

Les réglages sont conservés dans SQLite et réconciliés entre les serveurs ; leur convergence est éventuelle. Le répartiteur local consulte ses valeurs réconciliées et ses propres exécutions actives avant d'attribuer un travail. Un préfiltre évite qu’une bibliothèque pleine masque les travaux disponibles des autres bibliothèques ; les contrôles locaux avant attribution restent l’autorité de ce serveur. Diminuer une limite ne tue pas les travaux actifs et bloque seulement les nouveaux démarrages locaux.

## Surveillance

Une bibliothèque active avec un flow attribué est surveillée par défaut, toutes les 60 secondes. Le réglage peut arrêter la surveillance ou changer la période (15 à 86 400 secondes). La file de découverte reste durable, idempotente et distribuée aux nœuds ayant un accès direct en lecture. Plusieurs serveurs peuvent proposer les demandes ; l’index unique des découvertes actives empêche les doublons.

La découverte conserve taille, date de modification et moment de stabilisation. L’automate exige 30 secondes de stabilité et une modification datant d’au moins 30 secondes. Une réception par fichier, version de flow, taille et date est enregistrée atomiquement avec l’exécution. Les réceptions des traitements existants sont reprises lorsque les dates sont cohérentes. Une mise en file manuelle enregistre aussi cette réception pour éviter une seconde tâche automatique.

Un échec ou une annulation ne crée pas de boucle de relances automatiques : la relance reste explicite. Un changement de fichier ou de version attribuée rend le fichier de nouveau éligible. Arrêter la surveillance ne supprime pas les tâches déjà en file. Le chemin reste une donnée interne ; les résultats .encoding-results sont exclus des découvertes.

Ce mécanisme est un sondage périodique, pas un événement natif du système de fichiers. Un changement conservant simultanément taille et date de modification ne sera pas détecté. Pour les copies longues, utiliser un nom temporaire puis renommer le fichier terminé. La limite d’inventaire reste 10 000 fichiers.

## Interfaces et configuration

- Bibliothèque : action Surveillance et limites ; surveillance, période, plafond global et plafonds de ses workers accessibles.
- Nœud : action Limite globale ; plafond toutes bibliothèques.
- Les champs numériques passent par RecordForm avec validations communes. Les formulaires existants gardent leurs types text/select.
- Les écritures utilisent des révisions pour refuser les modifications concurrentes.

La migration 0014 répare les accents déjà enregistrés dans le registre et son journal ; les sources restent UTF-8. La migration 0015 introduit les réglages, la stabilité et les réceptions.

Le premier essai réel a montré que le verrou de fichier sur le montage Docker Desktop empêchait le transfert. Le verrou local est maintenant dans le volume privé du worker, indexé par le chemin source ; les réservations d'exécution restent locales au serveur qui les a attribuées. Les sources sont préservées. Les deux fichiers d’essai ont terminé l’inspection avec snapshots avant/après après cette correction.
