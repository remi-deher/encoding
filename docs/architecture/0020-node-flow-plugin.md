# 0020 — Plugin de contraintes d’attribution aux nœuds

## Décision

Le plugin intégré `nodes`, affiché « Nœuds », est disponible par défaut dans le catalogue et reconnu nativement par les workers. Il ne nécessite ni réglage de connexion ni processus tiers.

Les blocs du plugin expriment des contraintes sur le worker qui traitera le fichier entier :

- `nodes-run-on-node` limite l’attribution à l’identifiant d’un worker précis ;
- `nodes-require-capability` demande un outil, éventuellement une version minimale ou une capacité annoncée.

Le répartiteur vérifie ces contraintes au moment de réclamer une tâche. Si aucun worker n’est éligible, la tâche reste en attente. Plusieurs blocs ciblant des workers différents rendent le flow invalide.

## Transfert et limites

Les contraintes choisissent le worker d'un segment du flow. Le bloc explicite `nodes-handoff` permet de céder la suite à un autre worker selon l'architecture 0021, sans rejouer les étapes terminées. La configuration d'accès bibliothèque et le stockage durable des artefacts assurent le transfert des fichiers.

Le plugin ne contourne pas les contrôles existants d’éligibilité des outils, de rôle worker, de capacité, de limites de traitement ou d’accès à la bibliothèque.

## Opportunités ultérieures

Des blocs de priorité de tâche, de préférence souple pour une famille de workers et de reprise après échec sur un worker différent nécessiteraient chacun une politique explicite du répartiteur et un état durable. Ils ne sont pas implémentés par ces contraintes strictes.
