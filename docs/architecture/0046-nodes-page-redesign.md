# 0046 — Refonte de la page Nœuds : carte de ressource, tiroir à onglets, résumé

Statut : étapes 1, 2 et 3 implémentées (sélection multiple et barre de charge reportées, faute de données côté API). Demande de Rémi : « refonte totale de la page /nodes, du tiroir, des boutons et des cartes » ; maquette validée, conservée dans docs/design/nodes-redesign-proposal.html.

## Décisions

- La page répond à une seule question : quel est l'état de mes nœuds et que dois-je faire ? Trois plans : résumé du cluster, cartes de nœuds comparables, tiroir latéral pour agir sur un nœud.
- **Carte de ressource** (`app-resource-card`, composant partagé générique) : identité (titre, pastille d'état, rôles), charge optionnelle (barre de capacité), étiquettes (trois au plus, mises en avant d'abord, puis « +n »), faits, note et ouverture ; actions secondaires dans un menu. Aucune donnée propre à une page.
- La page Nœuds l'utilise pour sa grille ; les nœuds qui demandent une action (vidange, incompatible, quarantaine) sont classés avant les nœuds hors ligne, puis les nœuds en ligne, puis par nom.
- Hiérarchie de boutons Material : un bouton principal rempli par vue, tonal, contour, texte ; destructif en contour rouge et séparé.

## Étapes

1. **Fait** : carte de ressource, grille, tri « à traiter d'abord ». Les actions (nom et étiquettes, mode, suppression avec confirmation) et l'ouverture du détail sont inchangées.
2. **Fait** : le tiroir de détail partagé gagne un pied d'actions collant (`footerActions`, relayé par le gabarit de catalogue) ; la page Nœuds y place « Nom et étiquettes » et « Changer le mode ». Les onglets existants (Général, Traitement, Capacités, Bibliothèques, Avancé) sont conservés : ils couvrent déjà les besoins de la maquette ; l'onglet Rattachement est reporté tant que l'API ne donne pas le cluster par nœud.
3. **Fait** : le résumé du cluster et l’alerte d’approbation vivent dans le bandeau et les tuiles de 0047 ; filtre d’état (Tous, En ligne, À traiter, Hors ligne) cumulable avec le filtre de rôle. **Reporté** : sélection multiple et barre d’actions groupées, tant qu’aucune action groupée n’existe côté API.

## Limites

L'état de charge (tâches en cours) n'est pas exposé par l'API : la barre de capacité n'est pas affichée sur les nœuds tant qu'il manque. La sélection multiple de l'ancienne liste n'était branchée sur aucune action et n'est pas reprise ; elle reviendra à l'étape 3 avec ses actions.

## Complément : charge, sélection multiple et actions groupées

- `GET /api/v1/nodes/activity` renvoie en une requête les tâches actives (bail valide) par nœud ; la liste la rafraîchit avec les nœuds et les cartes des workers affichent « Tâches n / max » (barre de capacité).
- Les administrateurs peuvent sélectionner des nœuds ; une barre collante propose Vidanger, Reprendre le travail et Supprimer (avec confirmation). Chaque action appelle l'API unitaire existante pour chaque nœud et rapporte le nombre de réussites et d'échecs : pas d'endpoint groupé, donc pas de transaction globale.
- Filtre d'état (Tous / En ligne / À traiter / Hors ligne) cumulable avec le rôle.
