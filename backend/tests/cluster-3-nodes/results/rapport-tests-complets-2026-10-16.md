# Rapport de tests — suites complètes et cluster à 3 nœuds

**Date :** 16 octobre 2026
**Périmètre :** correctifs du worker, scénarios Runtime, contrats de plugins, tests Launcher, tests frontend/Python et exécution intégrée en cluster.

## Synthèse

Le correctif de complétion refusée est validé : le média corrompu passe à l’état `failed` après le refus HTTP 409, sans boucle de reprise. Les médias valides sont transcodés et décodés correctement. Le cluster a utilisé trois capacités déclarées différentes, `1`, `2` et `3` tâches simultanées.

Les scénarios de concurrence, de transfert, de perte serveur, de départ et de réinscription ont été exercés. La suite Runtime complète a compilé, mais n’a fourni aucun bilan après son démarrage et a été interrompue au bout de dix minutes. Les tests ciblés Runtime et les suites Plugins et Launcher ont fourni des résultats.

## Environnement du cluster

- 3 conteneurs Encoding indépendants et une base PostgreSQL dédiée, sur un réseau Docker isolé.
- `node1` : serveur + worker, capacité déclarée 1.
- `node2` : serveur + worker, capacité déclarée 2.
- `node3` : worker seul, capacité déclarée 3.
- Image locale construite à partir de `backend-encoding:latest`, avec les binaires compilés du workspace qui contiennent le correctif testé.
- Les trois nœuds ont les mêmes outils détectés et les mêmes encodeurs CPU utilisables : FFmpeg, ffprobe, mkvtoolnix, mediainfo, ImageMagick, libx264, libx265, libsvtav1 et libaom-av1. Aucun GPU n’était disponible.
- Le mode d’accès bibliothèque a varié : accès direct pour les trois nœuds, puis transfert depuis node1 pour node3 et format non pris en charge pour node2.
- Les nœuds partagent le même hôte Docker; ce test ne représente pas un stockage ou un réseau multi-hôte.

## Résultats cluster

| Scénario | Résultat | Observations |
|---|---|---|
| Démarrage, identités et état | Réussi | Les deux serveurs voient les mêmes 3 nœuds actifs; aucune quarantaine ni incompatibilité. Après la réinscription, la base conserve l’ancienne identité révoquée : 4 lignes historiques, 3 identités actives. |
| Capacités déclarées | Réussi | `node1=1`, `node2=2`, `node3=3`. Le sampler a observé des pics simultanés de 1, 2 et 2 tâches respectivement; la charge n’a pas saturé le troisième slot de node3. |
| Transcodage H.264/AAC | Réussi | Les 8 clips courts ont réussi. Les codecs, durées et décodages ont été vérifiés. |
| Soumissions concurrentes | Réussi | 8 demandes simultanées sur node1 et node2 : exactement 8 mises en file et 56 doublons ignorés. Aucun même média n’a été observé en exécution active sur deux workers dans les échantillons de ce scénario. |
| Média corrompu | Réussi après correctif | Échec terminal `failed`, époque 1, sans reprise répétitive. Même résultat dans les modes direct et transfert. |
| Transfert de fichiers | Réussi | Les 6 tâches sélectionnées ont eu node1 pour source; node3 les a traitées en transfert. Node2, configuré « non pris en charge », n’a reçu aucune de ces tâches. Le média corrompu du scan a échoué normalement. |
| Perte d’un worker | Défaut de reprise reproduit | Après l’arrêt de node3 et 75 s d’attente (au-delà du bail), son exécution restait attribuée à node3. Elle a repris à l’époque 2 après le retour de node3. Une deuxième soumission créée par le watcher a été exécutée séparément sur node1; elle ne correspondait pas à une reprise de la même exécution. |
| Perte d’un serveur | Réussi avec reprise différée | Pendant l’arrêt de node1, l’API de node2 a répondu 200 et une tâche de node2 s’est terminée. Les exécutions affectées à node1 et node3 ont avancé après le retour de node1. Node3 dépend de son URL de serveur unique, configurée vers node1. |
| Départ et réinscription | Réussi | Le départ pendant un bail renvoie 409; le départ propre renvoie 204; l’ancien jeton est refusé (410). La nouvelle identité de node3 est approuvée, puis son accès bibliothèque restauré; node3 reprend des tâches. |
| Sources et sorties | Réussi | Les 15 fichiers de la somme de contrôle sont restés intacts. 55 sorties réussies ont été contrôlées : vidéo H.264, audio AAC, durée à moins de 0,15 s de la source et décodage FFmpeg sans erreur. |

### Course du watcher

La surveillance de bibliothèque a repéré les nouveaux médias pendant la préparation. Certaines soumissions explicites et celles du watcher ont créé des exécutions distinctes pour un même nom de fichier. Les scénarios de perte ont été analysés par identifiant d’exécution et époque de bail pour séparer ces doublons de la reprise réelle.

## Résultats des tests automatisés

| Suite | Résultat |
|---|---|
| Runtime ciblé : complétion refusée / perte de worker, validation de flux, non-rejeu du plugin non idempotent, 409 au checkpoint et à la complétion finale | 7 tests réussis sur les deux exécutions ciblées. |
| Contrat `WaitBlockContractsAndCompatibility` | 1 réussi. |
| `Encoding.Plugins.Tests` (`dotnet run`, avec FFmpeg, ffprobe, Node, Python et PostgreSQL) | Réussi : catalogues et contrats de plugins, mappings, webhooks, scripts JavaScript/Python, memberships, triggers PostgreSQL et claims multi-serveurs. Les tests nécessitant Landlock ont été ignorés, ABI Landlock indisponible dans cet environnement. |
| `Encoding.Launcher.Tests` (`dotnet run`) | Tous les scénarios Launcher réussis : rôles, enrôlement local, supervision, redémarrage et endpoints. |
| Frontend (`npm test`) | Réussi précédemment : 60 tests Node et 22 tests Vitest sur 6 fichiers. Aucun fichier frontend n’a changé depuis cette exécution. |
| Tests Python (`python3 -m unittest discover -s backend/plugins/tests -v`) | Réussi précédemment : 33 tests. Aucun code Python de plugin n’a changé depuis cette exécution. |
| `dotnet test backend/Encoding.sln --configuration Release` | Compilation réussie. Le testhost Runtime n’a plus émis de résultats après son démarrage; le processus a été interrompu après 10 minutes. Aucun total fiable pour cette exécution complète. |
| `git diff --check` | Réussi. |

## Limites restantes

- Le défaut de reprise D2 persiste : l’exécution en cours sur un worker perdu reste liée à celui-ci jusqu’à son retour. Une soumission parallèle créée par le watcher ne constitue pas une reprise fiable de la même exécution.
- Le worker ne dispose que d’une adresse serveur configurée; node3 devient inactif quand node1 est indisponible même si node2 continue de servir le cluster.
- Les étapes d’intégration Sonarr/Radarr/Plex/Jellyfin n’ont pas été exécutées comme flux complet distribué dans ce cluster. Leurs contrats, webhooks et composants ont été couverts par `Encoding.Plugins.Tests`.
- Les scénarios qui exigent Linux Landlock ont été ignorés par l’environnement.
- La suite Runtime complète reste à diagnostiquer : le build passe, les tests ciblés passent, mais la commande complète est restée sans bilan pendant dix minutes.
