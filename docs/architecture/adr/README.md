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
| [0003](0003-publication-etape-de-flow.md) | Publication comme étape de flow, distincte de la fin du flow | implemented | flows |
| [0004](0004-reprise-au-checkpoint.md) | Reprise au checkpoint après perte du worker, blocs rejouables | implemented | runtime |
| [0005](0005-bloc-bibliotheque-sur-ce-noeud.md) | Bloc « Bibliothèque sur ce nœud » : brancher un flow selon l'accès direct | implemented | flows |
| [0006](0006-ressources-de-flow.md) | Ressources de flow : fichiers partagés du cluster (polices, images, audio) | accepted | flows |
| [0007](0007-decodage-unique-avant-publication.md) | Un seul décodage complet, juste avant la publication | accepted | runtime |
| [0008](0008-assistant-de-configuration-plex.md) | Assistant de configuration Plex : connexion plex.tv et correspondance des bibliothèques | implemented | plugins |
| [0009](0009-ecrivain-sqlite-unique-par-noeud.md) | Écrivain SQLite unique par nœud | implemented | replication-storage |
| [0010](0010-capture-incrementale-locale.md) | Capture incrémentale locale par clés modifiées | implemented | replication-storage |
| [0011](0011-controles-media-selon-effets.md) | Contrôles média selon les effets des opérations | implemented | runtime |
| [0012](0012-builder-ffmpeg-et-assemblage.md) | Builder FFmpeg explicite et assemblage MKV atomique | implemented | runtime |
