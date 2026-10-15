# 0035 — Jetons de design, thème sombre, cartes de données et éditeur de scripts

Statut : implémentée (demande de Rémi : usage strict de composants Material/CDK, lisibilité selon Material Design, centralisation des styles, tableaux denses en cartes, refonte de la partie scripts).

## Jetons de design

`frontend/src/styles.scss` définit les jetons partagés : `--space-1..6` (grille de 4 px), `--radius-sm|md|lg|xl|pill`, `--font-label|body|title|title-md|title-lg|headline`, `--elevation-1|2` et les couleurs sémantiques (`--accent*`, `--text-*`, `--danger*`, `--on-danger`…). Les styles de composants les consomment ; une valeur hors échelle est à éviter plutôt qu'à reproduire. Les contrastes du texte atténué respectent 4,5:1 sur les surfaces de l'application. Les composants Material suivent une palette tonale dérivée de la couleur de la marque (`$brand-palette`) plutôt que la palette verte de Material. Les pointeurs tactiles utilisent la densité Material standard, le focus visible et `prefers-reduced-motion` sont gérés globalement.

## Thème sombre

`ThemeService` (`core/theme`) pose `data-theme` sur `<html>` et mémorise le choix dans le stockage local ; à défaut il suit `prefers-color-scheme`. Un court script dans `index.html` applique le thème avant le premier rendu. `styles.scss` redéfinit les jetons sous `html[data-theme='dark']` et applique le thème Material sombre. Un composant doit utiliser les jetons, jamais une couleur en dur ; les couleurs de composants de flow issues du catalogue sont des paires texte/fond autonomes et restent valables dans les deux thèmes.

## Cartes de données

`DataTableComponent` prend un input `layout` (`table` par défaut, `cards`). Le rendu des cellules (bascule, pastilles, ouverture) est un gabarit unique partagé ; le tri, la sélection, les actions de ligne et de liste, les confirmations et le détail déplié sont ceux du tableau, déduits des mêmes définitions de colonnes. Les listes de gestion (flows, plugins, nœuds, groupes, bibliothèques, modèles, packs, profils, utilisateurs, scripts) utilisent `cards` ; les tableaux imbriqués dans une fiche et le suivi d'exécutions restent en `table`.

## Scripts de la bibliothèque

La page utilise le gabarit de catalogue : liste en cartes, éditeur dans le panneau latéral, avec l'historique des versions publiées (code en lecture seule chargé à l'ouverture, restauration dans le brouillon par l'action « Restaurer dans le brouillon », qui n'écrit rien tant que le brouillon n'est pas enregistré). La fermeture d'un brouillon modifié demande confirmation depuis le bouton Fermer ; la fermeture par la croix du panneau n'est pas encore interceptée (limite connue). Aucune évolution d'API ni de migration.

## Limites

Le thème sombre n'a été vérifié visuellement que sur l'en-tête, les listes en cartes et la page scripts ; l'éditeur de flow et les blocs de plugins restent à contrôler. Les cartes des listes nœuds et bibliothèques utilisent les colonnes actuelles ; une présentation plus riche (état de présence, capacités) reste possible.
