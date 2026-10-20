# 0077 — Vues Guidé / Canvas et assistant contextuel

## Décision

La refonte conserve un seul graphe métier dans la page d’édition : nœuds, configurations, connexions et instances de packs. Deux vues du composant partagé `FlowGraphEditor` émettent les mêmes commandes vers cette page. Aucun changement de vue ne réécrit les connexions.

- **Guidé** conserve les séquences, branches dépliables, suites communes, renvois et paramètres déclaratifs de 0010. Les colonnes ont une largeur minimale et s’étendent horizontalement dans le workspace ; elles ne sont plus comprimées ou empilées selon la profondeur. Le déplacement de séquences reste soumis aux invariants de 0010.
- **Canvas** permet le placement libre des blocs, le panoramique, le zoom et une surface extensible. Les positions sont des préférences locales du navigateur, indexées par identifiant de flow ; elles ne modifient ni la définition portable ni la sémantique d’exécution. Le placement initial est une projection déterministe qui conserve aussi les blocs détachés et les brouillons invalides.

Les ajouts, suppressions, duplications et modifications utilisent les mutations communes de la page. Une copie est initialement détachée et conserve les paramètres, sans copier les liaisons. Les entrées et les frontières de packs ne se dupliquent pas individuellement. Les packs restent des blocs composites ; leurs ports externes sont traduits vers les composants réels avant la validation et la mutation.

La palette utilise le glisser-déposer CDK dans les deux vues. Déposer sur une sortie en mode Guidé réutilise l’insertion commune, y compris le raccordement à la suite existante ; une sortie occupée refuse un bloc terminal sans suite. Déposer dans une zone vide crée un bloc détaché à relier explicitement. Canvas convertit le point de dépôt selon le zoom et le défilement et conserve sa position dans les préférences locales existantes. Un dépôt hors cible ne modifie pas le graphe ; les entrées et composants incompatibles ne sont pas déplaçables depuis la palette. Le clic et les actions clavier restent disponibles.

## Présentation et inspection

La palette est fermée par défaut. « Ajouter une étape » l’ouvre ; un ajout par clic la referme, sauf si elle est épinglée. L’ouverture de l’inspecteur ferme toujours la palette. Un clic sur le titre d’un bloc ouvre le tiroir partagé, contenu dans le workspace ; le flow reste visible à côté sur ordinateur. Les blocs sont allégés par défaut : titre, synthèse de trois paramètres scalaires déclarés inline au maximum et sorties. Les scripts, secrets et objets imbriqués restent dans l’inspecteur. Le menu permet de consulter les détails et de replier ou déplier les branches sans changer le graphe.

Le formulaire commun active dans l’éditeur une divulgation progressive : champs requis ou déclarés inline d’abord, autres champs dans « Réglages avancés ». Tous les contrôles restent dans le même formulaire, avec leurs valeurs et validations ; le rendu complet reste le défaut des autres usages. Les champs numériques de configuration activent le contrat numérique du champ partagé, tandis que les formulaires de fiches conservent leur contrat texte.

« Tester » lance une simulation avec le profil courant et annote les blocs parcourus avec la sortie choisie et les étapes supposées. Un arrêt ou une attente ouvre les détails ; une réussite laisse le flow visible. Le profil et les résultats détaillés restent accessibles dans le menu. Les badges sont retirés à chaque modification du graphe ; une réponse reçue pour un graphe devenu obsolète est ignorée. Les icônes de navigation sont des SVG locaux partagés, sans dépendance à une police distante.

## Liaisons et convergence

Les sorties du catalogue restent la source de vérité. Une seule liaison est permise par sortie, plusieurs sorties peuvent rejoindre une entrée et les cycles libres sont refusés par le validateur partagé. Dans Canvas, glisser d’une sortie libre vers une entrée ou sélectionner successivement les deux points réalise la même commande. Une sortie occupée doit être explicitement déconnectée avant un nouveau raccordement.

Les liaisons possèdent des actions accessibles par menu et au clavier. Guidé conserve le routage orthogonal avec évitement des cartes ; les chemins convergents rejoignent un même segment vertical avant l’entrée de l’étape commune. Les suites communes représentent des chemins alternatifs séquentiels, jamais une barrière de synchronisation parallèle.

## Actions contextuelles et historique

Le socle partagé de menu contextuel utilise Angular Material pour le clavier, le focus et la fermeture. Il s’ouvre au clic droit, avec Maj+F10 ou par maintien tactile de 550 ms. Un mouvement du doigt annule le maintien ; le clic qui suivrait un maintien reconnu est supprimé. Les boutons visibles restent disponibles pour les actions sur les étapes et liaisons.

L’historique de la page inclut les nœuds, connexions, configurations, packs et métadonnées. Annuler/Rétablir agit sur ce même état, quelle que soit la vue à l’origine du changement. Les opérations de runtime et de publication ne sont pas exécutées par cet historique.

## Assistant unique

Le widget existant et les menus d’étape ouvrent le même panneau d’assistant. La page prépare un instantané de la définition actuelle, y compris les changements non enregistrés et les packs, et indique la sélection. La sélection est une consigne explicite accompagnant le graphe complet ; elle ne tronque pas le contexte métier.

Le panneau réutilise les endpoints de génération, modification, explication et suivi IA existants. Il expose les différences de nœuds et connexions, l’aperçu du flow et la simulation, avant **Appliquer au flow** ou **Rejeter**. Une génération réussie n’applique rien automatiquement et ne publie rien. L’application vérifie l’instantané courant et refuse une proposition obsolète, y compris si une modification arrive pendant le chargement du catalogue. Une application réussie met à jour le graphe et les packs des deux vues et reste annulable. Le socle d’assistant général conserve ses autres usages.

Le panneau de flow est chargé à la demande pour ne pas alourdir le chargement initial de toutes les pages.

## Vérification

- `npm run lint`, `npm run build`, `npm test`, `npm run format:flows` dans `frontend/`.
- `PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npm run test:e2e -- e2e/flow-editor.spec.ts` pour les parcours ordinateur et mobile avec API fictive, sans médias ni fournisseur IA réels.
- Les tests de projection couvrent cinq niveaux de décision, les convergences, les composants détachés et la non-mutation du graphe. Les tests navigateur couvrent les mutations entre vues, les ports, l’historique, la revue IA et l’absence de défilement horizontal global.
