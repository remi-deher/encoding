# 0008 — Éditeur de flow Angular Material et CDK

## Décision

Le composant partagé FlowGraphEditor remplace ngx-vflow par des blocs Angular, les menus et boutons Angular Material, CDK Drag & Drop pour le déplacement et une couche SVG pour les connexions. Dagre conserve la disposition automatique. Le modèle métier et les événements publics du composant restent indépendants du rendu.

## Interactions

Le titre est la poignée de déplacement ; les boutons ne déclenchent pas de glissement. Les connexions suivent le déplacement. Les sorties libres proposent un menu pour ajouter un composant ou rejoindre un bloc existant, avec validation commune contre les cycles. Les sorties reliées peuvent être déconnectées individuellement.

Le fond se déplace par glissement. La molette et les boutons changent le zoom ; Cadrer ajuste le graphe. Un bloc focalisé se déplace avec les flèches, Entrée ouvre sa configuration et Supprimer retire un bloc autre que l’entrée. Les menus Material fournissent le parcours clavier de connexion. Le raccordement se fait par les menus de sortie ; les ports SVG n’ont plus de glissement pour créer un lien.

## Portée

La page utilise le même composant partagé et les mêmes contrats. Les dimensions utilisées par Dagre et les connexions sont centralisées dans flow-canvas.ts. La dépendance ngx-vflow est retirée. Le stockage et l’exécution des flows restent des capacités à implémenter.
