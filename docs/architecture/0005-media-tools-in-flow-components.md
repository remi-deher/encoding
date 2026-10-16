# 0005 — Outils média associés aux composants de flow

## Statut

Le catalogue des composants et l’exécution initiale des outils média sont implémentés. Les profils versionnés et le registre des encodeurs vidéo sont décrits dans [0027](0027-video-encoder-quality-profiles.md). Les autres paramètres média poursuivent leur évolution selon les mêmes contrats.

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

Le bloc « Audio par règles » (`base-audio-rules`, [0055](0055-audio-rules-block.md)) uniformise toutes les pistes audio avec FFmpeg et s’ajoute au plan commun.

Les identifiants des outils sont des métadonnées de catalogue. Un plugin fournit le composant ; l’outil indiqué décrit le moteur média que ce composant utilisera. Cette information ne prouve ni l’installation du binaire ni la compatibilité du worker.

Les définitions sont centralisées dans `frontend/src/app/shared/models/media-tool-actions.ts`, puis assemblées avec les composants de logique et d’intégration dans `flow-component-catalog.ts`. La page de l’éditeur ne décrit plus les actions métier. Les identifiants précédents restent conservés pour les points de départ existants.

Les actions de lecture exposent une variable de résultat et une option d’actualisation. Par défaut, elles réutiliseront les métadonnées du scan automatique prévu par [la décision 0006](0006-mandatory-media-scanning.md). Un rafraîchissement ne remplace jamais l’instantané initial de l’historique.

Les presets, qualités, codecs et identifiants de pistes sont des intentions de configuration. Pour la vidéo, les cibles par encodeur sont versionnées dans un profil et traduites par l’adaptateur d’exécution ; l’éligibilité compare le profil aux encodeurs vérifiés utilisables par le worker quand il annonce ces vérifications. Les anciennes capacités restent compatibles. Les identifiants de FFprobe, MediaInfo et MKVToolNix peuvent différer : la sélection de piste doit passer par un mapping commun. Les pistes extraites par MKVToolNix conservent leur codec, aucun champ de conversion n’est proposé pour cette action.

## Suite prévue

- Étendre le catalogue versionné aux autres paramètres média, capacités et résultats.
- Renforcer la détection active des périphériques matériels et estimer les limites de capacité par accélérateur.
- Étendre les adaptateurs aux autres codecs et paramètres de contrôle de débit sans construire de commande depuis une chaîne libre fournie par l’éditeur.
- Étendre la validation de sortie aux autres opérations avant publication selon les garanties de fichiers de `AGENTS.md`.

La disponibilité d’un encodeur vidéo reste distincte de sa déclaration dans le catalogue : l’agent l’annonce à partir de FFmpeg puis teste les encodeurs catalogués avec une mire synthétique, sans lire de média utilisateur. La capacité par périphérique et la mesure de débit restent à implémenter.
