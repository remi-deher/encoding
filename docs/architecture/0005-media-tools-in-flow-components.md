# 0005 — Outils média associés aux composants de flow

## Statut

Catalogue déclaratif implémenté dans l’éditeur. Le contrat et les adaptateurs d’exécution restent à implémenter ; les actions ne lancent pas encore les outils.

## Catalogue initial

| Outil | Opérations représentées dans l’éditeur |
| --- | --- |
| FFprobe | Lire le scan du média, lister les pistes, lire les caractéristiques vidéo et audio |
| FFmpeg | Encoder la vidéo et l’audio, normaliser le volume, redimensionner la vidéo, changer de conteneur, extraire l’audio, créer une miniature, vérifier le décodage |
| MKVToolNix (`mkvmerge`) | Créer/remuxer un MKV, ajouter audio et sous-titres, retirer et réordonner les pistes |
| MKVToolNix (`mkvextract`) | Extraire une piste dans son format natif, extraire les sous-titres, pièces jointes et chapitres |
| MKVToolNix (`mkvpropedit`) | Modifier les propriétés de piste et le titre du conteneur, retirer les pièces jointes, remplacer les chapitres, sur une copie de travail |
| MediaInfo | Lire le rapport complet, les caractéristiques HDR et celles d’une piste, exporter le rapport JSON |
| ImageMagick | Convertir, redimensionner et recadrer une image |

Les identifiants des outils sont des métadonnées de catalogue. Un plugin fournit le composant ; l’outil indiqué décrit le moteur média que ce composant utilisera. Cette information ne prouve ni l’installation du binaire ni la compatibilité du worker.

Les définitions sont centralisées dans `frontend/src/app/shared/models/media-tool-actions.ts`, puis assemblées avec les composants de logique et d’intégration dans `flow-component-catalog.ts`. La page de l’éditeur ne décrit plus les actions métier. Les identifiants précédents restent conservés pour les points de départ existants.

Les actions de lecture exposent une variable de résultat et une option d’actualisation. Par défaut, elles réutiliseront les métadonnées du scan automatique prévu par [la décision 0006](0006-mandatory-media-scanning.md). Un rafraîchissement ne remplace jamais l’instantané initial de l’historique.

Les presets, qualités, codecs et identifiants de pistes sont des intentions de configuration. Le futur contrat devra vérifier leur compatibilité avec l’encodeur effectif, les capacités du worker et le média ; il ne peut pas traduire aveuglément ces champs en arguments. Les identifiants de FFprobe, MediaInfo et MKVToolNix peuvent différer : la sélection de piste doit passer par un mapping commun. Les pistes extraites par MKVToolNix conservent leur codec, aucun champ de conversion n’est proposé pour cette action.

## Suite prévue

- Définir un contrat commun versionné pour décrire les outils, versions, capacités, paramètres et résultats.
- Faire détecter et annoncer les outils installés par chaque worker, puis utiliser ces capacités dans l’éligibilité et la répartition.
- Exécuter les outils sur des fichiers locaux au worker avec des arguments structurés, sans construire de commande depuis une chaîne libre fournie par l’éditeur.
- Valider les sorties avant leur publication selon les garanties de fichiers de `AGENTS.md`.

Le catalogue actuel est une aide de conception UI. Il ne réalise aucune détection, orchestration ni exécution des binaires.
