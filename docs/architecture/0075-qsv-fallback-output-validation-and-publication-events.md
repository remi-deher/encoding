# 0075 — Repli matériel vers CPU, validation de sortie et événement de publication

Statut : implémenté.

## Décision

- L'action vidéo expose `retryWithCpu`, désactivée par défaut. Lorsqu'elle est activée et qu'un encodeur matériel est choisi, un échec FFmpeg déclenche une seconde exécution CPU avec le même codec. Le fichier partiel est supprimé avant le nouvel essai et seules les étapes configurées pour le repli passent sur CPU. En présence d'un profil, le repli essaie d'abord ses cibles CPU, puis les encodeurs CPU compatibles disponibles ; sans profil, il utilise l'encodeur CPU correspondant. Une accélération matérielle indisponible détectée avant exécution sélectionne directement le CPU. La profondeur de couleur et les métadonnées HDR restent résolues depuis la configuration et la source.
- Le composant `probe-validate-output` appartient au plugin Probe, après les transformations et l'assemblage. Ses sorties sont `valid`, `invalid` et `error`. Il peut vérifier le codec vidéo, les comptes de pistes vidéo/audio/sous-titres, les codecs audio et sous-titres, les canaux et dispositions audio/LFE, les métadonnées des sous-titres, les chapitres, les pièces jointes requises ou d'origine, l'écart de durée, la croissance de taille, les événements ASS et le décodage. Chaque contrôle est configurable ; les différences intentionnelles peuvent donc être autorisées dans le flow.
- Après la confirmation atomique de publication par le nœud d'accès, le serveur enregistre un événement durable `media.published` dans `execution_events`, avec le chemin publié et le SHA-256. L'état de l'exécution reste `succeeded`. Le journal d'événements et la notification SSE exposent ce signal.
- Les conditions natives `conditions-video-codec`, `conditions-video-hdr` et `conditions-video-bitdepth` restent les outils de branchement pour conserver une vidéo déjà HEVC ou protéger des sources HDR/10-bit. Aucun nouveau bloc de condition n'est requis.

## Portée de l'événement

`media.published` est un événement durable observable par l'API et l'historique des exécutions. Les plugins Plex, Radarr et Sonarr ne le consomment pas automatiquement pour le moment. Leur déclenchement après publication nécessite un consommateur d'événements ajouté ultérieurement.

## Limites

La comparaison ASS générique rapproche les événements texte de la source et de la sortie. Elle ne compare pas la sortie aux fichiers ASS préparés ni au plan interne spécifique au flow FileFlows. Les contrôles configurables sont facultatifs pour permettre des changements voulus de pistes, de codecs, de chapitres ou de pièces jointes.
