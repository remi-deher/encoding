# 0051 — Encodage matériel dans l'image

## Contexte

FFmpeg listait `vaapi`, `qsv` et `nvenc`, mais l'initialisation VA-API échouait : l'image ne contenait aucun pilote VA-API (`iHD_drv_video.so` absent, seule `libvpl2` présente). Le catalogue d'encodeurs (0027) et la sonde d'encodeurs vérifiés existaient déjà ; il manquait les pilotes et un choix de périphérique unique.

## Décision

- **Intel** : `intel-media-va-driver-non-free` (multiverse, repli sur `intel-media-va-driver`), `libvpl2` + `libmfx-gen1.2` (QSV Gen12+), `libmfx1` (Media SDK, QSV Gen9.5 comme l'UHD 630) et `vainfo`. VAAPI est la voie principale sur Gen9.5.
- **AMD** : VA-API par Mesa (`mesa-va-drivers`, `libdrm-amdgpu1`). AMF n'est pas utilisé.
- **NVIDIA** : aucun pilote dans l'image. Le pilote propriétaire vient de l'hôte via le NVIDIA Container Toolkit ; l'image fixe `NVIDIA_VISIBLE_DEVICES=all` et `NVIDIA_DRIVER_CAPABILITIES=compute,video,utility`. La construction échoue si FFmpeg n'a pas NVENC (les paquets Ubuntu l'incluent via `ffnvcodec`).
- **Détection** : la sonde existante (`ToolCapabilityProbe`) reste l'unique source : un encodeur est « utilisable » seulement si une mire synthétique l'encode. Le mode automatique des profils (0027) classe NVENC, puis QSV, puis VAAPI, puis CPU, ce qui donne le repli sur `libx264`/`libx265`. `GpuDeviceSelector` choisit le périphérique de rendu pour la sonde et pour les commandes FFmpeg : `ENCODING_GPU_DEVICE` (chemin ou nom court), sinon le premier `/dev/dri/renderD*`, sinon `renderD128`. `LIBVA_DRIVER_NAME` est lu directement par libva (les processus FFmpeg héritent l'environnement).
- **Journal** : à chaque changement de situation matérielle, l'agent journalise les encodeurs matériels actifs, ou un avertissement listant la raison du repli CPU par famille (NVIDIA absent, aucun `renderD*`, VA-API inutilisable).
- **Déploiement** : `backend/docker-compose.gpu.yml` est une surcharge optionnelle (`/dev/dri`, réservation `nvidia`). Elle n'est pas dans le compose principal car un `/dev/dri` absent ou un hôte sans NVIDIA Container Toolkit empêchent le démarrage.

## Conséquences et risques

- Taille d'image : quelques dizaines de Mo (Mesa, pilotes Intel, Media SDK).
- Licence : `intel-media-va-driver-non-free` (multiverse) active les encodeurs Intel propriétaires ; l'image bascule sur la version libre (encodage limité) si le paquet est indisponible.
- Multi-GPU : un seul périphérique de rendu est utilisé par nœud ; le forcer par `ENCODING_GPU_DEVICE`.
- Non vérifié en conditions réelles dans le dépôt : exige un hôte avec GPU (commandes de vérification dans `backend/README.md`).
