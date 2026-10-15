# 0046 — Refonte de la page Nœuds : carte de ressource, tiroir à onglets, résumé

Statut : étape 1 implémentée (carte de ressource et grille) ; étapes 2 et 3 à venir. Demande de Rémi : « refonte totale de la page /nodes, du tiroir, des boutons et des cartes » ; maquette validée, conservée dans docs/design/nodes-redesign-proposal.html.

## Décisions

- La page répond à une seule question : quel est l'état de mes nœuds et que dois-je faire ? Trois plans : résumé du cluster, cartes de nœuds comparables, tiroir latéral pour agir sur un nœud.
- **Carte de ressource** (`app-resource-card`, composant partagé générique) : identité (titre, pastille d'état, rôles), charge optionnelle (barre de capacité), étiquettes (trois au plus, mises en avant d'abord, puis « +n »), faits, note et ouverture ; actions secondaires dans un menu. Aucune donnée propre à une page.
- La page Nœuds l'utilise pour sa grille ; les nœuds qui demandent une action (vidange, incompatible, quarantaine) sont classés avant les nœuds hors ligne, puis les nœuds en ligne, puis par nom.
- Hiérarchie de boutons Material : un bouton principal rempli par vue, tonal, contour, texte ; destructif en contour rouge et séparé.

## Étapes

1. **Fait** : carte de ressource, grille, tri « à traiter d'abord ». Les actions (nom et étiquettes, mode, suppression avec confirmation) et l'ouverture du détail sont inchangées.
2. Tiroir à onglets (Résumé, Rattachement, Capacités, Bibliothèques) avec pied d'actions collant.
3. Bandeau de résumé du cluster, alerte d'approbation, filtres d'état, sélection multiple.

## Limites

L'état de charge (tâches en cours) n'est pas exposé par l'API : la barre de capacité n'est pas affichée sur les nœuds tant qu'il manque. La sélection multiple de l'ancienne liste n'était branchée sur aucune action et n'est pas reprise ; elle reviendra à l'étape 3 avec ses actions.
