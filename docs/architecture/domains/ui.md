---
domain: ui
updated: 2026-10-26
legacy_adr:
  [
    0008,
    0010,
    0011,
    0035,
    0044,
    0046,
    0047,
    0048,
    0050,
    0052,
    0053,
    0073,
    0077,
    0078,
    0079,
    0080,
    0081,
  ]
code:
  - frontend/src/styles.scss
  - frontend/src/app/core/layout/
  - frontend/src/app/core/theme/
  - frontend/src/app/shared/ui/
  - frontend/src/app/shared/models/
  - frontend/src/app/features/
  - docs/design/
tags:
  [
    angular,
    material,
    thème,
    jetons,
    tableau,
    cartes,
    tiroir,
    éditeur de flow,
    canvas,
    page nœuds,
    shell,
    recherche contextuelle,
  ]
---

# Interface (Angular, Material 3)

Règles obligatoires : [UI_RULES.md](../../../UI_RULES.md). Ce fichier consigne l'état et les décisions durables ; maquettes et briefs dans `docs/design/`.

## Socle

- Angular + Angular Material/CDK uniquement. `frontend/src/styles.scss` : jetons `--space-1..6`, `--radius-*`, `--font-*`, `--elevation-*`, couleurs sémantiques ; thème clair/sombre (`ThemeService`, `data-theme`, suit `prefers-color-scheme`) ; **jamais de couleur ni de valeur hors échelle en dur**. Boutons Material en pilule ; un seul bouton rempli par vue, tonal pour l'alternative, contour pour le reste, destruction en rouge avec confirmation commune. Contrastes ≥ 4,5:1, focus visible, `prefers-reduced-motion`.
- Composants communs (toute évolution examine ses usages) : `DataTable` (`layout="table|cards"`, tri, sélection, actions de ligne/liste, confirmations), `RecordForm`/formulaire de schéma, `detail-drawer` (`size standard|wide`, `modal`, pied d'actions collant), `app-resource-card`, `app-resource-sheet`, `app-summary-tiles`/`app-metric-card`, `app-context-banner`, `PageHeader`, `GuidedSetup` (stepper), `MonitorTemplateComponent`, `ResourceCatalogTemplateComponent`, `LibrarySectionTabsComponent`, `FlowWorkspaceTemplateComponent`, `AssistantWorkspaceTemplateComponent` et `PlatformAdminTemplateComponent`. `MonitorTemplateComponent` cadre le pilotage et ses zones responsives ; `SummaryTiles` et `MetricCard` restent informatifs par défaut et acceptent une destination lorsque la mesure oriente vers une prise en charge. Les catalogues Construire partagent le gabarit et les lignes `ResourceList` ; l’espace de conception partage son cadre entre création et modification tout en gardant le graphe et ses outils spécialisés. L’assistant partage son shell d’historique, de conversation et de contexte avec son panneau compact ; les sources, citations et actions restent fournies par la feature IA. Paramètres partage son en-tête, sa navigation par domaine et son tiroir protégé, tandis que les pages de domaine gardent la portée, les formulaires et les règles d’enregistrement. Les tableaux imbriqués et le suivi d'exécutions restent en table.
- Le shell global (`AppShellComponent`) garde la navigation et la barre supérieure constantes entre les pages. Le corps du document est fixe ; la région principale est l’unique zone de défilement vertical du shell. Le pied de navigation réunit le repli, le compte et le contrôle d’apparence. Les modes d’authentification et d’éditeur plein écran conservent leurs cadres dédiés.
- `GlobalSearchBarComponent` réutilise `SearchFieldComponent` et affiche d’abord les résultats fournis par la page, puis les destinations globales. `PageShellContextService` permet à chaque page de fournir sa recherche, jusqu’à deux actions et un filtre ; les données, l’état des filtres et les règles métier restent dans leur feature. Les contrôles sans contexte ne sont pas rendus. La feuille est ancrée au champ, garde sa largeur et son alignement, et son voile commence sous le séparateur inférieur.
- `AppearancePaletteComponent` réunit les modes Clair/Sombre et les palettes Forêt, Océan, Iris et Rose. `ThemeService` persiste ces choix dans le navigateur. Le choix de palette colore aussi les jetons Angular Material exposés par le shell.

La décision de shell est enregistrée dans [ADR 0018](../adr/0018-shell-commun-et-recherche-contextuelle.md). Les pages seront raccordées au contrat de recherche et d’actions au fil des travaux métier ; le shell ne fournit actuellement que les destinations globales aux pages sans contexte enregistré.

- Les modèles et niveaux de raisonnement IA, les presets d'encodeurs et les catalogues métier **viennent du backend**, pas du frontend. Distinguer composants de flow, composants UI et nœuds de cluster dans le code.

## Éditeur de flow

- Un seul graphe métier, une vue **Canvas** dans `FlowGraphEditor` : placement libre, panoramique, zoom Ctrl+molette, minicarte et disposition verticale explicite et annulable. Les packs peuvent être dépliés dans le Canvas pour modifier leurs composants internes, puis repliés depuis sa barre d’outils. Les positions Canvas sont des préférences **locales au navigateur** (par flow), jamais dans la définition.
- Cartes Canvas adaptatives : hauteur 64 px ; largeur intrinsèque de 200 à 336 px pour le texte, élargie si les sorties exigent davantage de place (40 px par port). Icône de domaine, titre tronqué à la borne maximale, plugin et type ; résumé de trois paramètres scalaires `inline` au plus en infobulle au survol/focus du titre, jamais de script ni de secret. Couleurs communes par jetons clair/sombre, condition en vert ; sélection avec anneau et élévation, simulation en pastille compacte dont le détail est accessible au focus/clic.
- Entrée centrée en haut ; sorties en bas avec cibles de 32 px. Icônes uniquement pour les identifiants/libellés exacts reconnus (oui/succès, non, erreur, suite, ignoré, attente, médias), numéro pour les sorties inconnues ; « non » en ambre distinct d’une erreur technique en rouge. Libellé et description accessibles par ARIA/infobulle ; ajout `＋` au survol/focus du port (ou sélection tactile), bouton dédié et menu de liaison. La palette et la validation restent pilotées par le catalogue et `flow-graph.ts` : les icônes ne modifient jamais le sens d’exécution.
- Les mesures réelles (`ResizeObserver`) pilotent ancres SVG, obstacles, minicarte, centrage et placement automatique ; les estimations initiales restent compactes. Palette et poignée d’icône en glisser-déposer CDK ; Entrée ou double-clic pour configurer, Tab pour les ports ; minicarte inactive pendant la création d’une liaison pour ne pas intercepter un port situé dessous ; menu contextuel conservé (clic droit, Maj+F10, appui long 550 ms).
- Configuration : formulaire de schéma commun avec `importance` (basic/advanced), `inline`, `section`, visibilité conditionnelle (masquer garde la valeur), champs de référence qui conservent une référence inconnue, divulgation progressive dans l'éditeur seulement.
- Palette de composants : catégories fonctionnelles déclarées par `FlowComponentDescriptor.category` ; le serveur valide l'identifiant, l'éditeur fournit le libellé, une catégorie inconnue est rejetée et une catégorie absente (plugin tiers historique) apparaît sous « Utilitaires ». Les catégories ne dépendent ni du type de média ni du nom du plugin ; recherche globale et filtre par plugin restent indépendants.
- Assistant IA contextuel unique et testeur de flow : voir [ai.md](ai.md) et [flows.md](flows.md). Annuler/Rétablir porte sur le graphe, pas sur le runtime.
- Fiche du catalogue : aperçu lecture seule (même rendu, clé de placement distincte), versions, utilisation, validation, sauvegardes ; suppression avec tableau des bibliothèques détachées.

## Page Nœuds

Voir [cluster.md](cluster.md) : onglets **Ce nœud** / **Cluster**, cartes de membres (accélérateur, matériel déclaré, traitements), actions groupées, assistant de rattachement guidé, tiroir de fiche. Pas de charge CPU/GPU affichée tant qu'aucune source réelle n'existe. Un accélérateur seulement détecté (non vérifié par mire) n'est jamais présenté comme utilisable.

## Bibliothèques et page Activité

La liste des bibliothèques suit le catalogue Flows : fiche de détail dans un tiroir, lignes de ressources avec sélection et barre d'actions groupées. Les bibliothèques peuvent être activées, désactivées ou supprimées ; elles ne sont pas publiables.

Route `/activity` (anciennes `/tasks` et `/ai/activity` redirigées ; `?view=queue|active|errors|cancelled|history`, `?type=ai` et les vues `?view=operate|diagnose` conservés) : deux parcours explicites dans la même route. **Exploiter** montre la file et les traitements en cours ; seuls les traitements admissibles peuvent être annulés, avec annulation groupée. **Diagnostiquer** montre les échecs, annulations, résultats et demandes IA, y compris les demandes IA en cours sans action d’annulation. La relance est proposée depuis la fiche d’un échec après confirmation ; elle conserve l’échec dans l’historique puis ouvre la file **Exploiter**. Recherche, pagination par état et tiroir de détail restent communs. Pas de suppression groupée de l’historique.

- Les compteurs viennent du backend. Les listes sont demandées **par état** (25 éléments par état, pages suivantes par `offset`), afin qu'une grosse file n'écarte pas les terminés de la vue générale limitée. Recherche sur les éléments chargés ; chemin complet conservé dans la fiche, nom de fichier et bibliothèque dans la liste. Filtres d'état toujours visibles, synthèse via `SummaryTiles` (densité compacte optionnelle ; densité habituelle conservée pour les autres usages). Chaque liste conserve ses dernières données si une autre requête échoue.
- L'indicateur « jobs en cours » de l'en-tête ouvre un panneau CDK ancré sous le bouton : média, bibliothèque, flow, nœud, étape et progression, y compris les annulations en cours. Requêtes filtrées uniquement pendant son ouverture ; fermeture par Échap, clic extérieur ou bouton, focus restauré. Lien vers `/activity?view=active` ; une progression à 100 % conserve l'état serveur « En cours ».
- Suivi partagé : EventSource conserve sa reconnexion native, le sondage HTTP de secours intervient après 15 s sans événement ; une réponse HTTP ancienne ou en erreur ne remplace pas un événement récent. Les listes paginées sont sondées sans requêtes qui se chevauchent, avec délai maximal de 20 s et conservation des données connues. Les demandes IA sont consultées uniquement par les administrateurs dans **Diagnostiquer**, hors filtre « Traitements » ; elles ne sont pas rechargées dans **Exploiter**. Le filtre « Assistant IA » ne charge pas les listes d'exécutions. Les véritables indisponibilités sont signalées sans vider le journal ni annoncer un historique vide.

## Autres pages

Initialisation et accès (`/login`, `/setup`) : `AccessTemplateComponent` fournit le cadre public commun, la marque, l’introduction, l’indicateur de progression et le responsive. La connexion garde son formulaire compact ; le premier démarrage garde la fiche locale et le parcours de création de compte et de rattachement. Le setup conserve un fond clair uni et un halo en thème sombre. La fiche locale liste toutes les adresses IPv4/IPv6 avec leur interface, y compris boucle locale et interfaces virtuelles. L'« Accès au nœud » est distinct : URL publique/annoncée configurée, sinon origine du navigateur (hôte et port publié conservés en Docker bridge). Aucune adresse de passerelle n'est présentée comme IP LAN de l'hôte Docker.

Jonction depuis le premier démarrage : bouton explicite « Créer le compte et continuer » avant la recherche ou la vérification d’un cluster. Les informations du compte et le mode sont ensuite confirmés ; la vérification, l’envoi et les nouvelles tentatives restent dans l’assistant avec la même session. Le code se compare sur le cluster d’accueil. L’ouverture de l’application suit l’approbation ; une demande encore en attente propose « Suivre la demande dans Nœuds ».

Console de logs système (filtres de sévérité, recherche, pause, auto-scroll intelligent, export) ; scripts (liste en cartes, éditeur en tiroir, restauration dans le brouillon) ; IA & fournisseurs ; Plugins (configuration globale, Store).

## Mise à jour

La page `/updates`, présente dans la navigation Administration et accessible aux deux rôles, affiche la version installée, les notes embarquées et celles de la dernière release publique si elle est joignable. Les états « à jour », « nouvelle version disponible », développement et vérification indisponible sont distincts ; le texte des notes est interpolé comme texte, sans interprétation HTML. La vérification se relance automatiquement toutes les 15 minutes et conserve les données affichées en cas d’erreur. Le cache serveur dure 10 minutes. L’installation se fait par mise à jour de l’image Docker.

## Limites connues

- Thème sombre non vérifié visuellement partout (éditeur de flow, blocs de plugins) ; Nodes redesign non vérifié en navigateur réel (mobile, E2E complets).
- `DetailDrawer` traite la croix, Escape et le voile comme la même demande de fermeture ; `closeGuard` permet de confirmer l’abandon d’une saisie avant de fermer.
- Le rôle lecture masque Paramètres et l'enrôlement direct ; les autres boutons d'écriture répondent par un refus.

## Assistant de création de flow

Le mode « Assistant vidéo » de la création ouvre `/flows/guided` avec le formulaire déclaratif commun et une protection contre la perte de saisie. Général, vidéo, pistes, résultat puis récapitulatif : la génération backend est validée avant l'enregistrement d'un brouillon et l'ouverture de l'éditeur. Une erreur conserve les choix. Les modèles et la création avancée restent accessibles.

Les formulaires guidés communs utilisent un stepper vertical sur écran étroit hors tiroir ; les tiroirs conservent leur avancement dans le bandeau et leur pied d’actions.
