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
- Les flows sont créés indépendamment puis attribués à une bibliothèque via une version publiée précise. La découverte des fichiers est effectuée par un nœud en accès direct ; le lancement part des fichiers détectés dans la bibliothèque, sans saisie de chemin média. Voir [0016](docs/architecture/0016-library-workflow-and-discovery.md).
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

- Les tâches doivent être durables, observables et récupérables après la perte d’un nœud. Le registre des exécutions et leur journal sont stockés dans PostgreSQL ; voir [l’architecture 0009](docs/architecture/0009-execution-tracking.md). Le runtime utilise ce registre avec des baux et réservations transactionnels ; voir l’architecture 0015.
- L’attribution doit utiliser un mécanisme temporaire récupérable (par exemple un bail avec expiration) afin qu’une tâche abandonnée puisse être reprise.
- Une seule tâche à la fois peut modifier un média donné.
- Une sortie est d’abord écrite dans un emplacement temporaire, puis validée avant de remplacer ou publier le fichier de bibliothèque.
- Toute opération de remplacement doit préserver le fichier source jusqu’à validation réussie de la sortie.
- Les transferts doivent détecter les fichiers incomplets ou corrompus et permettre la reprise ou le nouvel essai sans publier une sortie invalide.
- Les opérations doivent être conçues pour être idempotentes ou détecter explicitement les répétitions.
- Les métadonnées d’origine sont capturées avant toute modification du média. Une entrée spécialisée réalise le scan immédiatement ; une entrée Fichier générique permet des scripts préparatoires avant l’analyse. Le moteur impose le scan avant la première opération qui exige les métadonnées ou transforme le média, puis analyse la sortie temporaire avant validation et publication. Les instantanés avant/après sont conservés dans l’historique. Voir [l’architecture 0006](docs/architecture/0006-mandatory-media-scanning.md).

## Flows, composants et plugins

- Le moteur de flow est une capacité centrale du produit. Les flows sont des graphes versionnés composés de nœuds configurables et de connexions entre sorties typées et entrées.
- Le catalogue des composants utilisables dans un flow est alimenté par les plugins.
- Les composants déclarent leurs sorties possibles. L’aiguillage est défini sur le composant lui-même ; seules les sorties reliées sont représentées comme des chemins dans l’éditeur partagé.
- L’éditeur visuel utilise des séquences verticales Angular Material/CDK, avec branches imbriquées repliables et suites communes. Le modèle métier reste un graphe indépendant de l’interface. Les raccordements non structurables sont conservés sous forme de renvois explicites ; aucune conversion silencieuse n’est autorisée. Voir [l’architecture 0010](docs/architecture/0010-structured-flow-editor.md).
- L’éditeur autorise les graphes orientés acycliques, avec une seule connexion par sortie et plusieurs connexions vers une même entrée pour faire converger des chemins alternatifs. Ces chemins sont séquentiels ; une reconvergence ne constitue ni du parallélisme ni une barrière de synchronisation. Les boucles seront des nœuds conteneurs avec un sous-graphe d’itération, jamais des cycles libres.
- Chaque flow possède exactement une entrée visible et obligatoire, sans connexion entrante. L’utilisateur choisit Fichier générique ou l’entrée spécialisée correspondant au type de flow. Les scripts préparatoires se placent après cette entrée et avant les transformations. Le catalogue ne propose que les composants compatibles avec le type de flow déclaré, y compris les opérations sur les pistes audio/sous-titres d’un flow vidéo et les composants communs. Voir [l’architecture 0007](docs/architecture/0007-flow-entry-and-alternative-paths.md).
- Le plugin `base` fournit les composants génériques Action, Condition et Variable. Une boucle sera ajoutée avec son sous-graphe d’itération et une sortie de fin.
- Le plugin `base` est installé et activé par défaut ; il fournit les composants de base nécessaires aux flows courants.
- Le plugin intégré `nodes` (affiché « Nœuds ») est installé et actif par défaut sur tous les workers. Il fournit les contraintes d’attribution et le relais explicite entre workers décrit dans 0021.
- Le plugin intégré `conditions` fournit un bloc natif de règles avec groupes ET / OU imbriqués et préréglages modifiables. Le champ déclaratif commun `condition-tree` est validé et évalué avec le catalogue partagé ; le bloc JavaScript historique reste disponible. Voir 0022.
- Les plugins Sonarr, Radarr, Plex et Jellyfin sont livrés dans l’image worker (processus Python, SDK dans `backend/plugins/`) ; leur déclencheur d’import est un webhook côté serveur qui passe par la découverte et la mise en file communes des bibliothèques. Un plugin doit pouvoir démarrer sous le bac à sable Landlock : pas d’exécutable .NET. Voir [0032](docs/architecture/0032-integration-plugins-and-sdk.md).
- Le bloc `base-wait` met un flow en attente durable (délai, état d'un worker, charge de travail ou condition de plugin) sans occuper de worker ; le média reste réservé et le flow reprend à l'étape suivante. Sans sortie « Délai dépassé » reliée, un délai dépassé fait échouer l'exécution. Voir [0033](docs/architecture/0033-flow-waits-and-time.md).
- Les plugins additionnels servent principalement à intégrer des services externes, notamment Plex, Sonarr, Radarr et Jellyfin. Ils peuvent aussi fournir des déclencheurs, actions ou composants de flow selon leur rôle.
- Chaque plugin déclare son identité, sa version, sa compatibilité et les composants qu’il fournit au moyen d’un contrat partagé et versionné.
- La configuration d’un plugin fournit des valeurs par défaut partagées par le cluster ; un flow peut les surcharger uniquement pour ce flow. Les réglages propres à une instance de composant restent distincts. Les valeurs effectives sont résolues lors de l’attribution d’une tâche. Voir [l’architecture 0019](docs/architecture/0019-plugin-configuration.md).
- La configuration d’un plugin et d’un composant est décrite par un schéma de champs déclaratif et versionné. Le frontend rend les types de champs pris en charge avec le gabarit commun ; un plugin ne fournit ni code Angular, ni HTML, ni JavaScript d’interface.
- Le gabarit de référence des blocs est le bloc structuré partagé : entrée supérieure, type/poignée/plugin alignés, titre centré, paramètres déclaratifs et sorties inférieures régulièrement espacées. Les champs inline du schéma pilotent l’édition directe, sans interface spécifique par plugin. Voir [l’architecture 0011](docs/architecture/0011-flow-block-template.md).
- Les valeurs de configuration d’un composant sont stockées sur l’instance du nœud de flow. Les valeurs globales du plugin et leurs dérogations par flow sont stockées séparément. Le schéma est une aide à l’édition ; le backend valide les valeurs avant leur enregistrement et leur exécution.
- Les plugins tiers s’exécutent dans un processus séparé de l’application hôte. Leur communication avec l’application passe par un contrat interprocessus versionné ; ils ne sont pas chargés directement dans le processus ASP.NET Core ou worker.
- À ce stade, l’isolation des plugins repose sur ce processus séparé. Ne pas imposer de conteneur dédié par plugin.
- Le moteur valide un flow selon les composants et versions disponibles avant son exécution. Une exécution référence une version précise de sa définition.
- Par défaut, le traitement complet d’un fichier est attribué à un worker éligible. Un bloc explicite `nodes-handoff` permet de céder la suite à un autre worker à un checkpoint durable, sous un nouveau bail, en conservant la réservation du média ; voir l’architecture 0021. Le moteur ne distribue pas implicitement chaque étape.
- Dans le code, distinguer les composants de flow des composants UI Angular et des nœuds de cluster.
- Les plugins ne contournent pas les garanties du système sur l’attribution des tâches, l’accès aux bibliothèques et l’intégrité des fichiers.

La décision de stockage des définitions de flow est décrite dans [l’architecture 0004](docs/architecture/0004-flow-definition-storage.md) : document JSON versionné stocké en PostgreSQL, avec sauvegardes de versions immuables dans un dossier persistant séparé.

## Outils média

- Organiser les adaptateurs et services d’intégration des outils par fonction média : vidéo (FFmpeg, MKVToolNix), image (ImageMagick), audio, etc.
- Les workers détectent et déclarent les outils, versions et capacités disponibles ; le répartiteur en tient compte pour sélectionner un worker.
- Gérer les exécutables dans les images ou mécanismes d’installation Docker adaptés à la plateforme. Ne pas versionner les binaires tiers dans le dépôt source.
- Garder les outils média derrière des interfaces remplaçables et testables.

Le catalogue initial des outils associés aux composants de flow est décrit dans [l’architecture 0005](docs/architecture/0005-media-tools-in-flow-components.md). Cette association éditoriale n’atteste pas qu’un binaire est installé sur un worker ; la détection, l’éligibilité et le contrat d’exécution restent à implémenter.

## Consignes de travail

- Avant une décision structurante, consulter ce fichier et les instructions plus proches des fichiers concernés.
- Préserver le modèle distribué et configurable décrit ici lors de toute proposition d’architecture.
- Documenter les nouvelles décisions d’architecture et mettre à jour ce fichier si les principes du projet évoluent.
- Ne jamais lancer une commande de suppression, de remplacement ou de migration destructive sur des médias sans protection explicite et validation du résultat.

## Authentification et rôles

Toute route de gestion exige une session ; les rôles sont `admin` et `viewer` (lecture seule). Les routes joignables sans session, l’autorisation des workers par jeton de nœud et le durcissement du jumelage sont décrits dans [0031](docs/architecture/0031-authentication-roles-and-pairing.md). Une nouvelle route est fermée par défaut ; ne l’ouvrir (`AllowAnonymous`, `AllowViewer`, `AllowNodeCredentials`) qu’avec une décision explicite et la mise à jour du test qui énumère les routes anonymes.

## Règles absolues de programmation de l’interface

Les règles de [UI_RULES.md](UI_RULES.md) sont obligatoires pour toute conception ou modification de l’interface. Les lire avant de travailler sur l’interface et les appliquer sans contournement propre à une page. En cas de doute sur un besoin métier, demander plutôt qu’inventer. Toute évolution d’un socle ou composant commun doit examiner ses usages et signaler son impact.

## Règles absolues de programmation du backend

Les règles de [BACKEND_RULES.md](BACKEND_RULES.md) sont obligatoires pour toute conception ou modification du backend. Avant d’ajouter une fonctionnalité, rechercher et réutiliser les capacités communes existantes ; ne pas créer d’implémentation parallèle ou spécifique à un appelant. Toute évolution d’un contrat ou composant commun doit examiner ses usages et signaler son impact.

## Règles absolues de tests et de CI

Les règles de [TEST_RULES.md](TEST_RULES.md) sont obligatoires. Le lint doit être exécutable en local et dans GitHub Actions ; les tests doivent protéger les fonctionnalités, les contrats et les garanties du cluster. Les E2E légers doivent pouvoir s’exécuter en local, tandis que les scénarios longs et lourds sont exécutés dans GitHub Actions.

## Préparation du moteur de flow

Les premiers contrats de définition, de catalogue plugin et d’invocation sont préparés dans Encoding.Contracts/Flows et Encoding.Contracts/Plugins. Le runtime worker est activé (architecture 0015) ; les endpoints de catalogue, validation et stockage sont décrits dans 0013 et 0014. Consulter [0012 — Préparation du moteur](docs/architecture/0012-flow-engine-foundation.md) avant leur implémentation ; réutiliser le registre d’exécutions existant et préserver les garanties de bail, d’accès et de validation des artefacts.

Le registre déclaratif Base et la validation backend sont implémentés : consulter [0013 — Catalogue et validation](docs/architecture/0013-flow-catalog-and-validation.md). La création d’une exécution utilise cette validation. Le runtime worker exécute Base et les plugins tiers configurés en processus séparés ; disponibilité d’un descripteur et capacité d’exécution sont distinctes.

Les versions validées des flows sont enregistrées dans PostgreSQL avec contrôle de concurrence. Une file transactionnelle écrit et vérifie leurs sauvegardes JSON dans un dossier persistant séparé. Le catalogue et l’éditeur utilisent ce stockage ; voir [0014 — Stockage et sauvegardes](docs/architecture/0014-flow-version-storage-and-backups.md). Les brouillons incomplets, leurs sauvegardes immuables et l’import/export JSON/ZIP sont implémentés ; la restauration utilise les actions du tableau.


## Runtime des flows

Le testeur de flow est séparé du runtime : il explore une définition validée avec un profil fictif/importé et des résultats explicitement supposés pour les étapes externes. Il réutilise l’évaluateur de conditions commun et ne crée ni exécution ni transfert. L’import lit uniquement MediaInfo dans un espace temporaire borné du serveur. Voir [0023](docs/architecture/0023-flow-simulation.md).

Consulter [0015 — Runtime et archives](docs/architecture/0015-flow-runtime-and-portable-archives.md). Les workers utilisent des baux de 60 secondes avec époque, réservations uniques par média et checkpoints transactionnels. Une invocation externe interrompue avant checkpoint exige une relance explicite. Les scripts et plugins nécessitent Linux Landlock et travaillent dans l’espace temporaire de l’exécution ; aucun repli sans restriction des fichiers n’est autorisé.

La publication conserve la source et crée un résultat distinct dans .encoding-results/<executionId>/. Le remplacement de la source n'existe que par la politique de bibliothèque explicite (désactivée par défaut) de [0030](docs/architecture/0030-source-replacement-policy.md) : sauvegarde vérifiée, bascule atomique, restauration pendant la période de grâce. Les volumes d’artefacts et sauvegardes doivent être partagés durablement par les serveurs d’un cluster multi-hôte. Le nettoyage automatique des artefacts est désactivé par défaut ; une rétention explicite et simulable existe, voir [0029](docs/architecture/0029-operations-observability-and-retention.md) (santé, métriques Prometheus, journaux JSON, migrations versionnées avec empreintes et numéros uniques). Exploitation : [supervision](docs/operations/monitoring.md), [sauvegarde et restauration](docs/operations/backup-restore.md).

Le retrait d’un nœud révoque son identité sans supprimer l’historique et refuse les baux actifs ; voir [0017](docs/architecture/0017-node-removal-and-capability-detail.md). Ne pas réactiver implicitement une identité retirée via un heartbeat ou un enrôlement.

Les packs de composants sont des fragments copiés d’une version publiée précise. L’éditeur les affiche comme blocs composites repliables, mais stocke les composants et liaisons internes dans le graphe aplati du flow avec une interface d’entrée/sorties validée. Les modifications d’une instance restent locales au flow ; voir [0026](docs/architecture/0026-component-packs.md).

La surveillance et les plafonds utilisent [0018](docs/architecture/0018-library-monitoring-and-processing-limits.md) : global nœud toutes bibliothèques, global bibliothèque tous nœuds, puis couple nœud/bibliothèque. Les limites sont des plafonds sans réservation. La surveillance périodique utilise des réceptions transactionnelles par fichier/version/empreinte taille-date pour éviter les répétitions automatiques.

La section **Librairies** regroupe les bibliothèques média et les scripts partagés. Les scripts ont des brouillons révisés et des versions publiées immuables ; les flows copient le code choisi et conservent sa référence de version. Voir [0024](docs/architecture/0024-script-library.md).

Les encodeurs vidéo sont décrits par un catalogue partagé et les profils de qualité versionnés associent une cible à chaque encodeur. Un flow embarque l’instantané du profil publié. L’agent distingue les encodeurs déclarés par FFmpeg de ceux vérifiés utilisables par une mire synthétique ; le mode automatique choisit parmi les encodeurs utilisables selon les priorités du profil. Le cluster et le worker appliquent la même politique. Le worker conserve la résolution appliquée dans les checkpoints et l’historique expose les options FFmpeg réellement retenues ; voir [0027](docs/architecture/0027-video-encoder-quality-profiles.md).

La section **Modèles** contient des copies immuables de versions publiées de flows. Un flow créé depuis un modèle reçoit un nouvel identifiant ; voir [0025](docs/architecture/0025-flow-templates.md).

Les groupes de nœuds sont manuels ou automatiques selon les caractéristiques déclarées du worker. Les étiquettes de groupe sont héritées par leurs membres ; les étiquettes propres au nœud prévalent. Les blocs de sélection appliquent ensemble le filtre de groupe et les étiquettes au moment de l’attribution ; voir [0034](docs/architecture/0034-node-groups-and-labels.md).
