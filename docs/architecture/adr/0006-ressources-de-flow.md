---
id: 0006
title: Ressources de flow : fichiers partagés du cluster (polices, images, audio)
status: proposed
domain: flows
date: 2026-10-22
decided_by: à décider (idée de Rémi le 2026-10-22 ; nom, périmètre et stockage à trancher)
supersedes: []
superseded_by: []
tags: [ressource, police, image, audio, catalogue, version, sha-256, distribution, éligibilité]
---

# 0006 — Ressources de flow : fichiers partagés du cluster

## Contexte
- Les polices d'un flow sont un dossier **local à chaque nœud** (`ENCODING_FONT_DIR`, un sous-dossier par jeu). Le worker l'annonce dans `inventory.fontSets` et l'éligibilité du bloc `subtitles-add-fonts` en dépend ([flows](../domains/flows.md), [runtime](../domains/runtime.md)).
- Constaté le 2026-10-22 sur les deux hôtes : sans jeu « Crunchyroll » sur un nœud, tout segment qui contient ce bloc n'est éligible nulle part et le flow reste en file ; la mise en place a demandé une copie manuelle par hôte, un montage de volume et un redéploiement de chaque application.
- Les fichiers binaires ne sont **jamais réconciliés** entre serveurs ([replication-storage](../domains/replication-storage.md)) ; seuls les scripts, modèles, profils et packs le sont, car ce sont des métadonnées.
- Rémi veut une catégorie de fichiers réutilisables (polices, images, audio…) dans la section Librairies et un sélecteur correspondant dans les blocs.

## Décision (proposée)
1. **Une ressource n'est pas une bibliothèque média.** Une bibliothèque est un dossier surveillé qui déclenche des traitements ; une ressource est un fichier que des flows consomment. Les règles d'accès par nœud, de surveillance, de plafonds et de remplacement ne s'appliquent pas aux ressources. Section distincte dans « Librairies », à côté des scripts partagés.
2. **Modèle** : ressource = identifiant, type (`font`, `image`, `audio`, `other`), nom, description. Chaque ressource a des **versions immuables** (comme les scripts et profils) : numéro, nom de fichier, taille, type MIME détecté, **SHA-256**. Un flow référence `resourceId` + version (+ empreinte) ; il ne suit jamais « la dernière ». Le jeu de polices est une ressource de type `font` regroupant plusieurs fichiers.
3. **Distribution** : un worker ne lit plus un inventaire disque ; le serveur du nœud lui sert la version par empreinte, avec contrôle d'intégrité et reprise, par le même canal que les artefacts d'exécution. Copie dans l'espace temporaire de l'exécution, jamais de chemin libre. Le bloc ne dépend plus de `fontSets` pour une ressource référencée ; `fontSets` reste le repli pour un bloc qui nomme un jeu local.
4. **Blocs** : `subtitles-add-fonts` (et les blocs d'image ou d'audio à venir) acceptent une référence de ressource. Le champ est un sélecteur du catalogue, validé par le backend (existence, type, version).
5. **Sécurité** : envoi réservé au rôle `admin` ; liste blanche de types et d'extensions (`.ttf`, `.otf`, `.ttc`, images, audio usuels), taille maximale par fichier et par ressource, contenu vérifié par signature et non par extension, nom de fichier normalisé, aucun chemin issu de l'utilisateur. Avertissement de licence à l'envoi pour les polices.
6. **Compatibilité de versions** : un composant qui accepte une ressource est livré dans une version mineure du plugin ; un agent plus ancien ne peut pas réclamer un segment qui en contient (même mécanisme que `nodes-library-access`). Les références historiques (`fontSet`) restent résolubles ; les flows existants ne changent pas.

## À trancher avant le code
- **Stockage et réplication des octets** (point bloquant) : (a) octets dans l'entité réconciliée, avec une taille maximale basse (polices et petites images seulement) ; (b) métadonnées réconciliées, octets dans un magasin adressé par empreinte sur chaque serveur et récupérés d'un pair à la demande ; (c) volume partagé comme les artefacts. Recommandation : (b) ; (a) si la limite de lot du journal le permet pour les polices. Cela contredit aujourd'hui l'invariant « binaires jamais répliqués » : l'invariant doit être modifié explicitement.
- Nom du produit : « Ressources » (recommandé) ou « Éléments » (se confond avec les composants de flow).
- Portée : toujours partagées par tout le cluster, ou certaines propres à un flow ?
- Types livrés d'emblée : polices seules, puis images et audio selon un cas d'usage concret.

## Alternatives écartées
- **Tout mettre dans une bibliothèque média** : mélange deux concepts (déclencheur de traitement et ressource consommée), impose des chemins locaux par nœud et expose la ressource à la surveillance et au remplacement.
- **Garder des dossiers locaux et automatiser la copie** (script ou outil d'administration) : ne résout ni la dérive entre nœuds, ni l'éligibilité, ni la traçabilité par version.
- **Embarquer les octets dans la définition du flow** : alourdit chaque version, duplique les fichiers, dépasse vite la limite de taille d'import et de document.

## Conséquences et risques
- Invariants touchés : « fichiers binaires jamais réconciliés » ([replication-storage](../domains/replication-storage.md)) ; toute nouvelle table décide « réconciliée » ou « locale » et inscrit les suppressions en tombstone.
- Compatibilité du cluster : versions mixtes gérées par la version mineure du plugin ; un nœud qui n'a pas encore la ressource la télécharge, il n'est donc plus exclu de l'éligibilité.
- Tests à écrire : empreinte et taille, fichier corrompu ou tronqué, reprise du téléchargement, ancien agent, version supprimée ou inconnue, perte d'un serveur pendant le transfert, non-contournement de la liste blanche, flow existant avec `fontSet`.
- Non vérifié : la limite réelle de taille d'un lot du journal de réconciliation, le coût de réplication de dizaines de mégaoctets, la cohérence avec les sauvegardes JSON de flows.
- Aucun code n'est livré par cette PR : décision et plan seulement.
