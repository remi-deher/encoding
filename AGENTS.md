# AGENTS.md

## Objectif du projet

Construire une application auto-hébergée pour gérer et distribuer le traitement de bibliothèques multimédias. Le premier cas d’usage est l’encodage de bibliothèques Plex à l’aide d’outils tels que FFmpeg, ffprobe et MKVToolNix. Des intégrations Plex et *ARR pourront être ajoutées sous forme de plugins.

Le projet s’inspire des outils d’automatisation de fichiers existants, tout en plaçant le fonctionnement en cluster et la haute disponibilité au cœur de sa conception.

## Stack technique retenue

- Backend : **C# avec ASP.NET Core sur .NET 10 LTS**.
- Interface : **Angular avec TypeScript**.
- Base de données : **PostgreSQL**.
- Déploiement : **Docker**.
- Ces choix sont validés. Ne pas les remplacer sans décision explicite prise avec le propriétaire du projet.
- Le frontend Angular est initialisé dans `frontend/` avec Angular Material comme base de composants d’interface partagés. La structure du backend, des plugins, des outils et des images Docker reste à définir ensemble.

## Modèle de nœud

- Le système est un cluster de nœuds configurables ; aucun serveur central permanent ne doit être requis.
- Chaque installation peut héberger les fonctions serveur, les fonctions worker, ou les deux.
- Les fonctions serveur fournissent l’interface, l’API, la configuration partagée et l’orchestration.
- Les fonctions worker exécutent les tâches compatibles avec leurs capacités locales : outils, codecs, CPU, GPU, mémoire et limites de concurrence.
- Un nœud serveur sans capacité de traitement reste un membre utile du cluster et peut participer à la haute disponibilité.
- Les instances serveur doivent pouvoir fonctionner ensemble sans créer d’états divergents ni attribuer simultanément une même tâche.

## Bibliothèques partagées

- Une bibliothèque est une définition logique commune au cluster, avec ses règles, profils et paramètres.
- Chaque nœud déclare séparément son accès à chaque bibliothèque. Les chemins locaux peuvent différer d’un nœud à l’autre.
- Un nœud sans point d’accès configuré à une bibliothèque ne peut pas traiter directement ses fichiers.
- L’accès doit préciser les capacités de lecture et d’écriture nécessaires à la tâche.
- Le répartiteur ne doit attribuer une tâche qu’à un ensemble de nœuds capables d’accéder au média, directement ou par le mécanisme de transfert décrit ci-dessous.

## Modes d’accès aux médias

Pour chaque bibliothèque et chaque nœud, prévoir les modes configurables suivants :

1. **Accès direct** : le nœud lit et écrit le média depuis un chemin local ou un stockage partagé monté.
2. **Non pris en charge** : le nœud ne peut pas traiter cette bibliothèque et ne doit pas recevoir de tâche correspondante.
3. **Transfert vers un worker** : un nœud qui a un accès direct au média transfère la source par WebDAV vers le nœud worker sélectionné. Le worker traite une copie dans son espace temporaire local, puis renvoie le résultat au nœud qui gère l’accès à la bibliothèque.

WebDAV est ici un canal de transfert de fichiers entre nœuds. Les outils d’encodage doivent travailler sur des fichiers locaux au worker, et non traiter directement un média distant via WebDAV.

## Tâches et intégrité des fichiers

- Les tâches doivent être durables, observables et récupérables après la perte d’un nœud.
- L’attribution doit utiliser un mécanisme temporaire récupérable (par exemple un bail avec expiration) afin qu’une tâche abandonnée puisse être reprise.
- Une seule tâche à la fois peut modifier un média donné.
- Une sortie est d’abord écrite dans un emplacement temporaire, puis validée avant de remplacer ou publier le fichier de bibliothèque.
- Toute opération de remplacement doit préserver le fichier source jusqu’à validation réussie de la sortie.
- Les transferts doivent détecter les fichiers incomplets ou corrompus et permettre la reprise ou le nouvel essai sans publier une sortie invalide.
- Les opérations doivent être conçues pour être idempotentes ou détecter explicitement les répétitions.

## Flows, composants et plugins

- Le moteur de flow est une capacité centrale du produit. Les flows sont des définitions versionnées composées d’étapes configurables avec des entrées et sorties typées.
- Le catalogue des composants utilisables dans un flow est alimenté par les plugins.
- Le plugin `base` est installé et activé par défaut ; il fournit les composants de base nécessaires aux flows courants.
- Les plugins additionnels servent principalement à intégrer des services externes, notamment Plex, Sonarr, Radarr et Jellyfin. Ils peuvent aussi fournir des déclencheurs, actions ou composants de flow selon leur rôle.
- Chaque plugin déclare son identité, sa version, sa compatibilité et les composants qu’il fournit au moyen d’un contrat partagé et versionné.
- Les plugins tiers s’exécutent dans un processus séparé de l’application hôte. Leur communication avec l’application passe par un contrat interprocessus versionné ; ils ne sont pas chargés directement dans le processus ASP.NET Core ou worker.
- À ce stade, l’isolation des plugins repose sur ce processus séparé. Ne pas imposer de conteneur dédié par plugin.
- Le moteur valide un flow selon les composants et versions disponibles avant son exécution. Une exécution référence une version précise de sa définition.
- Par défaut, le traitement complet d’un fichier est attribué à un worker éligible ; le moteur ne distribue pas chaque étape du flow séparément.
- Dans le code, distinguer les composants de flow des composants UI Angular et des nœuds de cluster.
- Les plugins ne contournent pas les garanties du système sur l’attribution des tâches, l’accès aux bibliothèques et l’intégrité des fichiers.

## Outils média

- Organiser les adaptateurs et services d’intégration des outils par fonction média : vidéo (FFmpeg, MKVToolNix), image (ImageMagick), audio, etc.
- Les workers détectent et déclarent les outils, versions et capacités disponibles ; le répartiteur en tient compte pour sélectionner un worker.
- Gérer les exécutables dans les images ou mécanismes d’installation Docker adaptés à la plateforme. Ne pas versionner les binaires tiers dans le dépôt source.
- Garder les outils média derrière des interfaces remplaçables et testables.

## Consignes de travail

- Avant une décision structurante, consulter ce fichier et les instructions plus proches des fichiers concernés.
- Préserver le modèle distribué et configurable décrit ici lors de toute proposition d’architecture.
- Documenter les nouvelles décisions d’architecture et mettre à jour ce fichier si les principes du projet évoluent.
- Ne jamais lancer une commande de suppression, de remplacement ou de migration destructive sur des médias sans protection explicite et validation du résultat.

## Règles absolues de programmation de l’interface

Les règles de [UI_RULES.md](UI_RULES.md) sont obligatoires pour toute conception ou modification de l’interface. Les lire avant de travailler sur l’interface et les appliquer sans contournement propre à une page. En cas de doute sur un besoin métier, demander plutôt qu’inventer. Toute évolution d’un socle ou composant commun doit examiner ses usages et signaler son impact.

## Règles absolues de programmation du backend

Les règles de [BACKEND_RULES.md](BACKEND_RULES.md) sont obligatoires pour toute conception ou modification du backend. Avant d’ajouter une fonctionnalité, rechercher et réutiliser les capacités communes existantes ; ne pas créer d’implémentation parallèle ou spécifique à un appelant. Toute évolution d’un contrat ou composant commun doit examiner ses usages et signaler son impact.

## Règles absolues de tests et de CI

Les règles de [TEST_RULES.md](TEST_RULES.md) sont obligatoires. Le lint doit être exécutable en local et dans GitHub Actions ; les tests doivent protéger les fonctionnalités, les contrats et les garanties du cluster. Les E2E légers doivent pouvoir s’exécuter en local, tandis que les scénarios longs et lourds sont exécutés dans GitHub Actions.
