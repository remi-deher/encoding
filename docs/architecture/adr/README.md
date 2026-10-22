# Registre des décisions (ADR)

Les ADR de cette série commencent à **0001** (la série historique est retirée, voir [legacy-adr-map.md](../legacy-adr-map.md) ; les numéros de commentaires du code comme « 0049 » désignent l'ancienne série).

## Quand écrire un ADR
Une décision structurante : nouveau principe, changement d'un invariant des fiches de domaine, choix entre plusieurs alternatives, dérogation à une règle d'`AGENTS.md`. Pas pour documenter une fonctionnalité : cela va dans la fiche du domaine.

## Règles
- Un fichier `NNNN-sujet-court.md`, numéro suivant libre (consulter ce dossier **et les PR ouvertes** avant d'en prendre un : deux ADR ne partagent jamais un numéro).
- Court : contexte (≤ 10 lignes), décision, alternatives écartées, conséquences et risques. Utiliser [TEMPLATE.md](TEMPLATE.md).
- Statuts autorisés : `proposed`, `accepted`, `implemented`, `superseded`, `abandoned`. Un ADR remplacé n'est pas réécrit : on ajoute `superseded_by` et un nouvel ADR.
- Une décision de Rémi est citée avec sa date ; elle n'est pas modifiée sans son accord.
- Dès qu'un ADR est `implemented`, la fiche du domaine devient la référence et mentionne l'ADR dans ses sources.

## Registre

| Numéro | Titre | Statut | Domaine |
| --- | --- | --- | --- |
| [0001](0001-replication-connexions-ia.md) | Réplication chiffrée des connexions IA | implemented | ai |
| [0002](0002-modification-mkv-sur-place.md) | Modification MKV sur place par bloc dédié | implemented | runtime |
