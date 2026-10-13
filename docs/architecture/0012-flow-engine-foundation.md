# 0012 — Préparation du moteur de flow

## Statut et périmètre

Les contrats C# sont compilables. Le registre et la validation des définitions sont maintenant exposés par l’API (voir 0013), mais aucun worker ne consomme encore les invocations. Leur format reste à stabiliser avant publication. Aucun traitement média, chargement de plugin, bail ou remplacement de fichier n’est activé par leur ajout.

## Capacités existantes à réutiliser

- Encoding.Contracts : contrats partagés entre API et agent.
- ExecutionRepository : définition JSON figée pour chaque tentative, transitions transactionnelles, événements idempotents et révision attendue.
- NodeRepository et heartbeats : présence et capacités annoncées des workers.
- LibraryNodeAccessRepository : accès propres à chaque nœud et bibliothèque.
- ToolCapabilityProbe : détection des outils locaux de l’agent.
- Schémas de configuration et gabarit de bloc Angular : text, number, boolean, select et inline.

Le registre d’exécution reste unique. Le moteur ne crée pas une deuxième file ou un historique parallèle. Ses rapports passent par RecordAsync après contrôle du bail et des résultats.

## Contrats préparés

### Définition portable

FlowDefinition contient formatVersion, id, version, name, mediaType, nodes et connections. La version du format est distincte de la version métier immuable. Chaque bloc référence une version exacte du plugin, du composant et de son schéma. La configuration contient uniquement les valeurs de l’instance ; ni code UI ni définition de composant embarquée.

La sérialisation réseau et les sauvegardes devront utiliser les options web System.Text.Json (camelCase). Les types du média restent video, image, music et subtitles, tandis que le domaine d’un composant peut également être audio ou general, comme dans le catalogue actuel.

### Catalogue fourni par les plugins

PluginManifest décrit le contrat de l’hôte accepté, la version du plugin et ses composants. Chaque composant déclare son type, ses médias compatibles, ses sorties, son schéma et ses besoins en outils, capacités et accès média. Les listes de types compatibles seront explicites ; le registre devra normaliser le catalogue actuel avant de l’exposer au frontend.

Le backend valide les champs et les valeurs avec les mêmes règles métier que l’éditeur. Il rejette les versions, types de champ, identifiants et sorties inconnus. Le JSON d’une configuration ne rend pas une valeur exécutable ou valide par lui-même.

### Invocation et résultat d’étape

FlowStepInvocation identifie la tentative, le worker, le bloc, l’invocation et l’époque du bail. Le contexte transporte des références d’artefacts locaux, les métadonnées avant/courantes et les variables. Les références sont résolues par l’hôte dans l’espace de travail du worker ; un plugin ne choisit pas un chemin de publication dans la bibliothèque.

FlowStepResult fournit soit une sortie déclarée, soit un résultat terminal. Le moteur doit vérifier l’exclusivité, la version du contrat, l’identité de l’invocation, l’époque du bail, les variables autorisées et l’artefact candidat. Un résultat Success n’autorise pas à publier un fichier sans validation. Deferred exigera une politique de report explicite avant d’être supporté par le runtime.

## Règles du parcours

1. Une entrée unique, un graphe acyclique et une connexion au maximum par sortie.
2. Plusieurs sorties distinctes peuvent rejoindre une entrée. Elles sont des chemins alternatifs : le moteur suit une seule sortie par étape et ne lance pas les branches en parallèle.
3. Plusieurs blocs terminaux sont autorisés. Seul celui atteint par le parcours termine la tentative.
4. Une sortie déclarée non reliée termine ce chemin. La réussite de la tentative nécessite encore la validation des artefacts produits. Une erreur non reliée échoue ; elle ne devient pas une réussite implicite.
5. Un résultat de plugin choisissant une sortie inconnue échoue comme erreur de contrat. Une panne du processus ou du transport est une erreur d’infrastructure ; elle ne devient pas automatiquement la sortie métier error.
6. L’entrée média spécialisée scanne immédiatement. Une entrée Fichier générique autorise des scripts préparatoires ; le premier composant qui exige des métadonnées provoque le scan obligatoire avant son exécution. Le scan avant transformation et le scan après production sont conservés dans l’historique.
7. La reprise d’un effet externe incertain n’est jamais automatique. Une invocation répétée doit être reconnue comme déjà accomplie, être idempotente, ou être bloquée pour décision explicite.

## Mise en place par étapes

### 1. Registre et validation

Installer Base par défaut ; adapter les définitions du catalogue actuel au contrat ; centraliser le registre des versions disponibles. Implémenter la validation du graphe, des versions et configurations côté backend. Retourner FlowValidationIssue avec code, chemin JSON et message. Brancher ensuite l’éditeur sur ce catalogue.

### 2. Enregistrement des flows

Créer le dépôt PostgreSQL de définitions versionnées, les endpoints brouillon/publication et le service de sauvegarde JSON atomique décrit dans 0004. Écrire un travail de sauvegarde rejouable dans la transaction de publication ; afficher les échecs de sauvegarde. Les définitions d’exécution doivent provenir d’une version validée et immuable plutôt que d’un document arbitraire envoyé à POST executions.

### 3. Attribution récupérable

Étendre le registre existant avec un bail borné, une époque croissante, un propriétaire et les checkpoints d’étape. Réserver le média au niveau cluster dans la même attribution transactionnelle. Sélectionner un worker capable de réaliser tout le flow et d’accéder à la bibliothèque. Renouvellement, expiration et rapports doivent vérifier l’époque pour rejeter un ancien worker.

L’annulation est contrôlée avant chaque étape et pendant les processus externes. Un arrêt n’est confirmé qu’après arrêt du processus et nettoyage contrôlé de l’espace temporaire. La perte du bail interdit toute publication.

### 4. Runtime local et plugins isolés

Introduire le runtime dans NodeAgent, avec les adaptateurs d’outils par fonction média. Base peut être une capacité interne de l’hôte ; les plugins tiers s’exécutent en processus séparé. Prévoir un handshake versionné, invocation/cancel/résultat corrélés, limites de message et délai, sans charger de code tiers dans ASP.NET Core.

Le transport interprocessus précis reste à définir avant implementation. Les contrats préparés décrivent les messages métier, sans imposer un conteneur par plugin.

### 5. Intégrité et publication

Acquérir les fichiers en accès direct ou par transfert WebDAV vérifié. Traiter localement une copie ou une sortie temporaire. Scanner/valider la sortie, puis publier uniquement avec un bail et une réservation média encore valides. Conserver la source jusqu’à validation et remplacement réussis. Écrire les métadonnées avant/après et le résultat définitif dans le registre existant.

## Vérifications à prévoir lors de l’implémentation

Compatibilité JSON, schémas et versions, reconvergence et fins multiples, worker inéligible, deux serveurs concurrents, bail expiré, ancien rapport, événement dupliqué, annulation, panne de plugin, effet incertain, sauvegarde manquante et sortie invalide. Utiliser des petits médias synthétiques, jamais une bibliothèque utilisateur.
