# 0047 — Socle de la refonte de la page Nœuds : boutons, tuiles, bandeau de contexte et tiroir de fiche

Statut : implémentée (demande de Rémi : refonte de la page Nœuds, de son tiroir, des boutons et des cartes). Complète 0046 (cartes de ressource) ; la maquette est dans `docs/design/`.

## Décision

- **Boutons** : tous les boutons Material prennent la forme en pilule (Material 3) par les variables `--mat-button-*-container-shape` de `styles.scss`. La hiérarchie reste : un seul bouton rempli par vue, tonal pour l'alternative proche (« Groupes de nœuds »), contour pour le reste, destruction en rouge avec confirmation commune.
- **Cartes de liste** (`DataTable`, `layout="cards"`) : rayon `--radius-xl`, survol qui élève la carte et teinte sa bordure. S'applique à toutes les listes en cartes.
- **Tuiles de synthèse** : `app-summary-tiles` (valeur et légende, jamais d'action) sur `app-metric-card`, qui gagne `tone="attention"` pour une décision attendue. Emplacement `[catalogue-summary]` du gabarit de catalogue, vide et masqué ailleurs.
- **Bandeau de contexte** : `app-context-banner` dit où l'on se trouve (nœud autonome ou cluster) et porte l'action « Ce nœud et son cluster », qui n'est plus cachée dans les actions de page.
- **Tiroir** (`detail-drawer`) : coins arrondis côté page. Deux entrées nouvelles : `size="standard"` (560 px, fiche) ou `wide` (défaut, jusqu'à 1 000 px, éditeur) et `modal` (voile : un clic à l'extérieur ferme). Seule la page Nœuds les active ; les éditeurs avec brouillon (scripts) gardent le tiroir large sans voile, leur fermeture par la croix n'étant pas interceptée (0035).
- Onglets de la fiche : en-tête collé en haut pendant le défilement.

## Impacts à surveiller

Les boutons en pilule et les cartes de liste changent d'apparence sur toutes les pages. Le gabarit de catalogue gagne un emplacement et le tiroir deux entrées, avec des valeurs par défaut qui conservent le comportement précédent.

## Limites

Le pied d'actions collant du tiroir (Annuler / Enregistrer) et le panneau « Ce nœud et son cluster » redessiné restent à faire ; ce dernier attend la PR du parcours de rattachement (0046-pending-join).
