---
id: 0030
title: Migration des composants et plugins historiques vers le Store
status: accepted
domain: plugins
date: 2026-10-30
decided_by: Rémi (2026-10-30)
supersedes: []
superseded_by: []
tags: [store, composant, plugin, migration]
---

# 0030 — Migration des composants et plugins historiques vers le Store

Contexte : ADR [0029](0029-store-plugins-outils-hashes.md). La migration des outils binaires reste à terminer.

## Décision

Le dépôt public unique `remi-deher/encoding-store` distribue les plugins de processus et les manifestes des composants média. Aucun paquet plugin/composant n'est préinstallé par défaut. Les plugins Plex, Sonarr, Radarr et Jellyfin s'exécutent toujours dans un processus séparé sous Landlock. Les familles `video`, `audio`, `subtitles`, `mkv`, `images`, `probe` et `ai` utilisent un runtime `native` réservé à cette liste : le paquet fournit le manifeste descriptif, tandis que le code reste dans le moteur C# et passe par ses handlers existants.

Les composants fondamentaux `base`, `nodes` et `conditions` restent fournis par l'application. Les binaires média auront des archives versionnées par plateforme, avec hash d'archive et de chaque exécutable, et seront associés aux plugins par rôle d'outil et plage de versions. Tant que ces archives ne sont pas publiées et installables, les outils apt de l'image restent un repli de compatibilité.

## Conséquences

- Les descriptions de composants et les scripts/manifests des quatre intégrations sont retirés du catalogue et de l'image de l'application ; le dépôt public conserve les sources, SDK, tests ou manifestes déplacés.
- Un flow qui référence l'une de ces familles exige son paquet présent sur le serveur qui l'édite/attribue et sur le worker qui l'exécute. L'installation et la synchronisation sont locales à chaque nœud.
- Les tests de comportement du moteur ne dépendent pas de la présence implicite d'un paquet Store ; ils chargent des fixtures déclaratives explicites. Les tests et sources Python des intégrations sont maintenus dans le dépôt Store.
- Le retrait des binaires de l'image attend les paquets autonomes pour les plateformes supportées et la validation de leurs dépendances système.

## Limites

Le hash assure l'identité des octets distribués, pas leur provenance. La signature des catalogues/paquets, la publication des outils, et une validation sur plusieurs plateformes et matériels restent à faire.
