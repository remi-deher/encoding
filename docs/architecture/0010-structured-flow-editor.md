# 0010 — Éditeur de flow structuré

## Décision

Le composant partagé FlowGraphEditor présente des séquences verticales avec branches Angular Material repliables et réorganisation de listes CDK. Cette décision remplace le canevas SVG de 0008 et la disposition Dagre. Les dépendances et le modèle de coordonnées propres au canevas sont retirés. Les positions historiques restent stockées sur les nœuds pour compatibilité ; elles ne pilotent plus l’interface.

Le catalogue, les champs déclaratifs, les types de média compatibles, les noms d’instances et la validation commune sont conservés. Les plugins fournissent leurs sorties ; l’interface n’impose pas deux branches fixes.

## Projection et raccordements existants

flow-sequence.ts projette le graphe sans le modifier. Chaque étape affiche un bouton par sortie déclarée. Seules les sorties reliées créent des branches ; une sortie alternative unique conserve son libellé de branche. Les branches configurées se partagent la largeur disponible et leurs blocs remplissent leur colonne. Les branches profondes et les petits écrans reviennent à une colonne. Les autres étapes deviennent des conteneurs de branches lorsqu’elles ont plusieurs sorties reliées. La première étape commune que tous les chemins reliés doivent traverser est affichée après les branches. Une sortie non reliée termine explicitement son chemin ; elle ne rejoint pas implicitement la suite.

Les raccordements partagés qui ne peuvent pas être représentés par une seule imbrication restent des renvois vers une étape conservée. L’interface signale ce cas et permet de consulter/configurer l’étape. Aucune conversion automatique ne réécrit le flow. Les blocs non reliés à l’entrée restent visibles dans une section dédiée.

## Édition

Ajouter une étape sur une sortie occupée l’insère avant la cible existante. Une étape terminale ne peut pas être insérée devant une suite. Ajouter une suite aux chemins non terminés relie les extrémités libres des branches configurées à la nouvelle étape ; les terminaux explicites restent terminaux.

Une séquence simple se réordonne avec CDK ou les boutons Monter/Descendre. L’opération réécrit atomiquement les raccordements adjacents ; elle refuse de traverser l’entrée, une condition, un aiguillage, une étape partagée ou une sortie autre que la continuation principale. Les glissements entre branches ne sont pas autorisés.

Supprimer une étape avec une seule sortie reliée reconnecte ses prédécesseurs à sa suite. Supprimer un aiguillage conserve les étapes des branches, qui peuvent devenir détachées. Les sorties peuvent être déconnectées ou reliées à une étape existante par les menus accessibles au clavier.

## Portée

Le socle partagé remplace le rendu de la page d’édition. Le zoom, le cadrage, les coordonnées libres et le bouton Organiser sont supprimés ; l’organisation découle des raccordements. L’enregistrement des flows et leur exécution par les workers restent des fonctionnalités distinctes encore à réaliser.


## Surface et limites des branches

Les routes d’édition utilisent le mode workspace du socle : pleine largeur, hauteur de fenêtre et défilement principal unique. Le contenu principal des drawers délègue ce défilement au parent ; les panneaux de configuration ont leur défilement propre lorsqu’ils sont ouverts.

Toutes les sorties ont le même rôle de chemin. Une sortie reliée unique reste lisible comme une séquence, avec son libellé affiché entre les étapes. Plusieurs sorties reliées créent les colonnes de branches. Ajouter dans une branche ne relie que la sortie choisie ; aucune sortie de la nouvelle étape n’est connectée automatiquement lorsqu’elle est ajoutée en fin de chemin. Une insertion devant une étape existante conserve explicitement cette suite. Les suites communes apparaissent uniquement lorsque les raccordements convergent réellement. Les sous-étapes s’arrêtent à cette convergence et affichent le retour à la suite.


## Liaisons et poignées

Une couche SVG affiche les raccordements visibles et se recalcule après les changements de graphe et de dimensions. Les branches repliées masquent leurs raccordements internes. L’affichage des liaisons peut être désactivé.

Pour relier deux étapes, sélectionner le point d’une sortie libre puis le point d’entrée d’une étape compatible. La validation de graphe commune reste obligatoire. Une sortie occupée doit être déconnectée explicitement avant modification. Ce parcours fonctionne également au clavier avec les boutons ; les points de liaison ne sont pas des poignées de déplacement.

Le glissement CDK d’une étape utilise désormais un bouton dédié visible, avec le symbole ⠿ et un libellé accessible. Les étapes dont le déplacement est interdit ne présentent pas de fausse poignée active.


## Configuration et couleurs

Le flow est centré avec une largeur maximale de lecture de 1200 px dans la surface workspace, dont le défilement reste unique. La configuration s’ouvre par double-clic sur un bloc, ou avec Tab puis Entrée. Les boutons internes conservent leurs propres actions et ne déclenchent pas cette ouverture. Le bouton Configurer et son entrée de menu sont retirés.

flow-presentation.ts centralise les couleurs par type et plugin. La bordure et l’étiquette indiquent le type ; le libellé souligné indique le plugin. Les plugins inconnus reçoivent une couleur déterministe. Les libellés textuels restent présents, sans dépendre uniquement des couleurs.

## En-tête de l’espace d’édition

L’en-tête est placé avant le drawer/canvas et utilise la disposition `workspace` du composant partagé `PageHeader`. Il expose le titre, l’état de sauvegarde et les outils Tester, Vérifier et Publier. Les actions Annuler puis Enregistrer sont alignées à droite ; Enregistrer sauvegarde le brouillon, Publier crée une version publiée. Sur mobile, les outils se replient sur plusieurs lignes et les actions restent groupées à droite. Les autres pages utilisent la disposition par défaut du même composant.
