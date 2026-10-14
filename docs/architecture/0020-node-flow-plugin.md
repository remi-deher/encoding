# 0020 — Plugin de contraintes d’attribution aux nœuds

## Décision

Le plugin intégré `nodes`, affiché « Nœuds », est disponible par défaut dans le catalogue et reconnu nativement par les workers. Il ne nécessite ni réglage de connexion ni processus tiers.

Les blocs du plugin expriment des contraintes sur le worker qui traitera le fichier entier :

- `nodes-run-on-node` limite l’attribution à l’identifiant d’un worker précis ;
- `nodes-require-capability` demande un outil, éventuellement une version minimale ou une capacité annoncée.

Le répartiteur vérifie ces contraintes au moment de réclamer une tâche. Si aucun worker n’est éligible, la tâche reste en attente. Plusieurs blocs ciblant des workers différents rendent le flow invalide.

## Transfert et limites

Une étape d’un flow ne peut pas céder une exécution en cours à un autre worker. Le runtime attribue tout le traitement du fichier à un worker, conserve son bail et ses checkpoints, et traite le média dans son espace local. La configuration d’accès bibliothèque détermine l’accès direct ou le transfert WebDAV du média vers ce worker. Le bloc « Exécuter sur un nœud précis » choisit donc le worker avant le démarrage ; il ne déclenche pas un transfert à mi-parcours.

Le plugin ne contourne pas les contrôles existants d’éligibilité des outils, de rôle worker, de capacité, de limites de traitement ou d’accès à la bibliothèque.

## Opportunités ultérieures

Des blocs de priorité de tâche, de préférence souple pour une famille de workers et de reprise après échec sur un worker différent nécessiteraient chacun une politique explicite du répartiteur et un état durable. Ils ne sont pas implémentés par ces contraintes strictes.
