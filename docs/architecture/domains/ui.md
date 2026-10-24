---
domain: ui
updated: 2026-10-24
legacy_adr: [0008, 0010, 0011, 0035, 0044, 0046, 0047, 0048, 0050, 0052, 0053, 0073, 0077, 0078, 0079, 0080, 0081]
code:
  - frontend/src/styles.scss
  - frontend/src/app/shared/ui/
  - frontend/src/app/shared/models/
  - frontend/src/app/features/
  - docs/design/
tags: [angular, material, thème, jetons, tableau, cartes, tiroir, éditeur de flow, canvas, page nœuds]
---

# Interface (Angular, Material 3)

Règles obligatoires : [UI_RULES.md](../../../UI_RULES.md). Ce fichier consigne l'état et les décisions durables ; maquettes et briefs dans `docs/design/`.

## Socle
- Angular + Angular Material/CDK uniquement. `frontend/src/styles.scss` : jetons `--space-1..6`, `--radius-*`, `--font-*`, `--elevation-*`, couleurs sémantiques ; thème clair/sombre (`ThemeService`, `data-theme`, suit `prefers-color-scheme`) ; **jamais de couleur ni de valeur hors échelle en dur**. Boutons Material en pilule ; un seul bouton rempli par vue, tonal pour l'alternative, contour pour le reste, destruction en rouge avec confirmation commune. Contrastes ≥ 4,5:1, focus visible, `prefers-reduced-motion`.
- Composants communs (toute évolution examine ses usages) : `DataTable` (`layout="table|cards"`, tri, sélection, actions de ligne/liste, confirmations), `RecordForm`/formulaire de schéma, `detail-drawer` (`size standard|wide`, `modal`, pied d'actions collant), `app-resource-card`, `app-resource-sheet`, `app-summary-tiles`/`app-metric-card`, `app-context-banner`, `PageHeader`, `GuidedSetup` (stepper), gabarit de catalogue. Les listes de gestion sont en cartes ; les tableaux imbriqués et le suivi d'exécutions restent en table.
- Les modèles et niveaux de raisonnement IA, les presets d'encodeurs et les catalogues métier **viennent du backend**, pas du frontend. Distinguer composants de flow, composants UI et nœuds de cluster dans le code.

## Éditeur de flow
- Un seul graphe métier, une vue **Canvas** dans `FlowGraphEditor` : placement libre, panoramique, zoom Ctrl+molette, minicarte et disposition verticale explicite et annulable. Les packs peuvent être dépliés dans le Canvas pour modifier leurs composants internes, puis repliés depuis sa barre d’outils. Les positions Canvas sont des préférences **locales au navigateur** (par flow), jamais dans la définition.
- Blocs compacts : type et plugin visibles, synthèse de trois paramètres scalaires `inline` au plus, jamais de script ni de secret dans le résumé ; entrées en haut, sorties nommées en bas ; la validation de connexions est centralisée (`flow-graph.ts`). Palette en glisser-déposer CDK ; clic et clavier toujours disponibles ; menu contextuel (clic droit, Maj+F10, appui long 550 ms).
- Configuration : formulaire de schéma commun avec `importance` (basic/advanced), `inline`, `section`, visibilité conditionnelle (masquer garde la valeur), champs de référence qui conservent une référence inconnue, divulgation progressive dans l'éditeur seulement.
- Assistant IA contextuel unique et testeur de flow : voir [ai.md](ai.md) et [flows.md](flows.md). Annuler/Rétablir porte sur le graphe, pas sur le runtime.
- Fiche du catalogue : aperçu lecture seule (même rendu, clé de placement distincte), versions, utilisation, validation, sauvegardes ; suppression avec tableau des bibliothèques détachées.

## Page Nœuds
Voir [cluster.md](cluster.md) : onglets **Ce nœud** / **Cluster**, cartes de membres (accélérateur, matériel déclaré, traitements), actions groupées, assistant de rattachement guidé, tiroir de fiche. Pas de charge CPU/GPU affichée tant qu'aucune source réelle n'existe. Un accélérateur seulement détecté (non vérifié par mire) n'est jamais présenté comme utilisable.

## Bibliothèques et page Activité
La liste des bibliothèques suit le catalogue Flows : fiche de détail dans un tiroir, lignes de ressources avec sélection et barre d'actions groupées. Les bibliothèques peuvent être activées, désactivées ou supprimées ; elles ne sont pas publiables.

Route `/activity` (anciennes `/tasks` et `/ai/activity` redirigées, filtres `?view=` et `?type=ai` conservés) : une seule liste pour les exécutions de flows et les demandes de l'assistant IA, groupée par état, avec filtres, recherche et tiroir de détail. Seules les exécutions sélectionnées dans un état compatible sont opérables : relancer les échecs, annuler celles en attente ou en cours. Les demandes IA et les exécutions terminées n'ont pas d'action groupée ; l'historique d'exécution n'a pas d'opération de suppression.

- Les compteurs viennent du backend. Les listes sont demandées **par état** (25 éléments par état, pages suivantes par `offset`), afin qu'une grosse file n'écarte pas les terminés de la vue générale limitée. Recherche sur les éléments chargés ; chemin complet conservé dans la fiche, nom de fichier et bibliothèque dans la liste. Filtres d'état toujours visibles, synthèse via `SummaryTiles` (densité compacte optionnelle ; densité habituelle conservée pour les autres usages). Chaque liste conserve ses dernières données si une autre requête échoue.
- L'indicateur « jobs en cours » de l'en-tête ouvre un panneau CDK ancré sous le bouton : média, bibliothèque, flow, nœud, étape et progression, y compris les annulations en cours. Requêtes filtrées uniquement pendant son ouverture ; fermeture par Échap, clic extérieur ou bouton, focus restauré. Lien vers `/activity?view=active` ; une progression à 100 % conserve l'état serveur « En cours ».
- Suivi partagé : EventSource conserve sa reconnexion native, le sondage HTTP de secours intervient après 15 s sans événement ; une réponse HTTP ancienne ou en erreur ne remplace pas un événement récent. Les listes paginées sont sondées sans requêtes qui se chevauchent, avec délai maximal de 20 s et conservation des données connues. L'activité IA n'est demandée qu'aux administrateurs et hors filtre « Traitements » ; ses sondages ne s'annulent plus mutuellement. Le filtre « Assistant IA » ne charge pas les listes d'exécutions. Les véritables indisponibilités sont signalées sans vider le journal ni annoncer un historique vide.

## Autres pages
Premier démarrage (`/setup`) : fond clair uni, halo conservé en thème sombre. La fiche locale liste toutes les adresses IPv4/IPv6 avec leur interface, y compris boucle locale et interfaces virtuelles. L'« Accès au nœud » est distinct : URL publique/annoncée configurée, sinon origine du navigateur (hôte et port publié conservés en Docker bridge). Aucune adresse de passerelle n'est présentée comme IP LAN de l'hôte Docker.

Jonction depuis le premier démarrage : bouton explicite « Créer le compte et continuer » avant la recherche ou la vérification d’un cluster. Les informations du compte et le mode sont ensuite confirmés ; la vérification, l’envoi et les nouvelles tentatives restent dans l’assistant avec la même session. Le code se compare sur le cluster d’accueil. L’ouverture de l’application suit l’approbation ; une demande encore en attente propose « Suivre la demande dans Nœuds ».

Console de logs système (filtres de sévérité, recherche, pause, auto-scroll intelligent, export) ; scripts (liste en cartes, éditeur en tiroir, restauration dans le brouillon) ; IA & fournisseurs ; Plugins (configuration globale, Store).

## Mise à jour
La page `/updates`, présente dans la navigation Administration et accessible aux deux rôles, affiche la version installée, les notes embarquées et celles de la dernière release publique si elle est joignable. Les états « à jour », « nouvelle version disponible », développement et vérification indisponible sont distincts ; le texte des notes est interpolé comme texte, sans interprétation HTML. La vérification peut être relancée et conserve les données affichées en cas d’erreur. Le cache serveur dure 10 minutes. L’installation se fait par mise à jour de l’image Docker.

## Limites connues
- Thème sombre non vérifié visuellement partout (éditeur de flow, blocs de plugins) ; Nodes redesign non vérifié en navigateur réel (mobile, E2E complets).
- La fermeture d'un tiroir de script par la croix n'est pas interceptée.
- Le rôle lecture masque Paramètres et l'enrôlement direct ; les autres boutons d'écriture répondent par un refus.

## Assistant de création de flow
Le mode « Assistant vidéo » de la création ouvre `/flows/guided` avec le formulaire déclaratif commun et une protection contre la perte de saisie. Général, vidéo, pistes, résultat puis récapitulatif : la génération backend est validée avant l'enregistrement d'un brouillon et l'ouverture de l'éditeur. Une erreur conserve les choix. Les modèles et la création avancée restent accessibles.

Les formulaires guidés communs utilisent un stepper vertical sur écran étroit hors tiroir ; les tiroirs conservent leur avancement dans le bandeau et leur pied d’actions.
