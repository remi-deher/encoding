---
domain: ui
updated: 2026-10-21
legacy_adr: [0008, 0010, 0011, 0035, 0044, 0046, 0047, 0048, 0050, 0052, 0053, 0073, 0077, 0078, 0079, 0080, 0081]
code:
  - frontend/src/styles.scss
  - frontend/src/app/shared/ui/
  - frontend/src/app/shared/models/
  - frontend/src/app/features/
  - docs/design/
tags: [angular, material, thème, jetons, tableau, cartes, tiroir, éditeur de flow, guidé, canvas, page nœuds]
---

# Interface (Angular, Material 3)

Règles obligatoires : [UI_RULES.md](../../../UI_RULES.md). Ce fichier consigne l'état et les décisions durables ; maquettes et briefs dans `docs/design/`.

## Socle
- Angular + Angular Material/CDK uniquement. `frontend/src/styles.scss` : jetons `--space-1..6`, `--radius-*`, `--font-*`, `--elevation-*`, couleurs sémantiques ; thème clair/sombre (`ThemeService`, `data-theme`, suit `prefers-color-scheme`) ; **jamais de couleur ni de valeur hors échelle en dur**. Boutons Material en pilule ; un seul bouton rempli par vue, tonal pour l'alternative, contour pour le reste, destruction en rouge avec confirmation commune. Contrastes ≥ 4,5:1, focus visible, `prefers-reduced-motion`.
- Composants communs (toute évolution examine ses usages) : `DataTable` (`layout="table|cards"`, tri, sélection, actions de ligne/liste, confirmations), `RecordForm`/formulaire de schéma, `detail-drawer` (`size standard|wide`, `modal`, pied d'actions collant), `app-resource-card`, `app-resource-sheet`, `app-summary-tiles`/`app-metric-card`, `app-context-banner`, `PageHeader`, `GuidedSetup` (stepper), gabarit de catalogue. Les listes de gestion sont en cartes ; les tableaux imbriqués et le suivi d'exécutions restent en table.
- Les modèles et niveaux de raisonnement IA, les presets d'encodeurs et les catalogues métier **viennent du backend**, pas du frontend. Distinguer composants de flow, composants UI et nœuds de cluster dans le code.

## Éditeur de flow
- Un seul graphe métier, deux vues du composant `FlowGraphEditor` qui émettent les mêmes commandes : **Guidé** (séquences verticales, branches repliables, suites communes, renvois explicites pour les raccordements non structurables, jamais de conversion silencieuse) et **Canvas** (placement libre, panoramique, zoom Ctrl+molette, minicarte, disposition verticale explicite et annulable). Les positions Canvas sont des préférences **locales au navigateur** (par flow), jamais dans la définition.
- Blocs compacts : type et plugin visibles, synthèse de trois paramètres scalaires `inline` au plus, jamais de script ni de secret dans le résumé ; entrées en haut, sorties nommées en bas ; la validation de connexions est centralisée (`flow-graph.ts`). Palette en glisser-déposer CDK ; clic et clavier toujours disponibles ; menu contextuel (clic droit, Maj+F10, appui long 550 ms).
- Configuration : formulaire de schéma commun avec `importance` (basic/advanced), `inline`, `section`, visibilité conditionnelle (masquer garde la valeur), champs de référence qui conservent une référence inconnue, divulgation progressive dans l'éditeur seulement.
- Assistant IA contextuel unique et testeur de flow : voir [ai.md](ai.md) et [flows.md](flows.md). Annuler/Rétablir porte sur le graphe, pas sur le runtime.
- Fiche du catalogue : aperçu lecture seule (même rendu, clé de placement distincte), versions, utilisation, validation, sauvegardes ; suppression avec tableau des bibliothèques détachées.

## Page Nœuds
Voir [cluster.md](cluster.md) : onglets **Ce nœud** / **Cluster**, cartes de membres (accélérateur, matériel déclaré, traitements), actions groupées, assistant de rattachement guidé, tiroir de fiche. Pas de charge CPU/GPU affichée tant qu'aucune source réelle n'existe. Un accélérateur seulement détecté (non vérifié par mire) n'est jamais présenté comme utilisable.

## Autres pages
Console de logs système (filtres de sévérité, recherche, pause, auto-scroll intelligent, export) ; scripts (liste en cartes, éditeur en tiroir, restauration dans le brouillon) ; IA & fournisseurs ; Plugins (configuration globale, Store).

## Limites connues
- Thème sombre non vérifié visuellement partout (éditeur de flow, blocs de plugins) ; Nodes redesign non vérifié en navigateur réel (mobile, E2E complets).
- La fermeture d'un tiroir de script par la croix n'est pas interceptée.
- Le rôle lecture masque Paramètres et l'enrôlement direct ; les autres boutons d'écriture répondent par un refus.
