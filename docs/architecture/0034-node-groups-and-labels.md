# 0034 — Groupes de nœuds et étiquettes

Statut : implémentée.

## Décision

Le cluster stocke des groupes nommés et décrits. Un groupe est soit manuel (ensemble de membres explicites), soit automatique (critères d’inventaire évalués à la lecture et à l’attribution). Les critères disponibles portent sur les rôles, le système d’exploitation, l’architecture, la mémoire, le nombre de processeurs, les outils déclarés et la concurrence annoncée.

Chaque nœud conserve un nom d’affichage et ses étiquettes locales séparément du nom transmis par heartbeat. Les groupes portent eux aussi des étiquettes. Les étiquettes effectives d’un nœud sont l’union déterministe des groupes auxquels il appartient, puis ses propres étiquettes qui prévalent pour une clé déjà définie. Une étiquette sans valeur représente la présence de la clé.

Le bloc `nodes-run-on-node` peut contraindre un segment par identifiant de nœud, identifiant de groupe et/ou conditions d’étiquettes. Le relais `nodes-handoff` peut cibler un autre worker par groupe et/ou étiquettes. Ces filtres se cumulent avec ET. Les groupes automatiques et les étiquettes effectives sont résolus au moment de l’attribution ; le résultat n’est pas figé lors de la publication du flow.

## Inventaire

Le heartbeat et le jumelage transmettent le système, l’architecture du processus, le nombre de processeurs disponibles et la mémoire totale disponible rapportée par le runtime. Ces valeurs sont observées et servent aux critères de groupes ; elles ne garantissent pas une disponibilité instantanée de ressources. Les outils média restent déclarés par le mécanisme de capacités existant.

## Stockage et accès

La migration `0030_node_groups_and_labels` ajoute l’inventaire, les étiquettes et l’identité d’affichage aux nœuds, plus les groupes et leurs membres. Les mutations passent par les routes de gestion protégées par session administrateur et protection CSRF. Les lectures sont accessibles avec une session viewer. La suppression d’un groupe retire ses étiquettes héritées ; elle ne modifie pas les nœuds ni leur historique. Un groupe ciblé par une version publiée ou un brouillon de flow ne peut pas être supprimé (409) : le flow ne trouverait plus aucun worker éligible. Les nœuds retirés ne sont plus listés comme membres d’un groupe manuel.
