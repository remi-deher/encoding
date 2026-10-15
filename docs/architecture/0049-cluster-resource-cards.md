# 0049 — Cartes horizontales des membres et demandes

Statut : implémentée après validation de la maquette.

La page Nœuds affiche le nom local dans Ce nœud et le nom du cluster dans Cluster (Aucun cluster si autonome). Le bandeau de résumé et les tuiles redondantes sont retirés. Les actions de création, rattachement et suivi restent dans l’en-tête ; les alertes de connexion et les motifs d’échec sont conservés dans le contenu.

Les membres et demandes de rattachement sont des cartes pleine largeur. Le composant commun DataTable reçoit une présentation déclarative facultative `resourceCards`, rendue par ResourceStrip (Material Card, ProgressBar, Checkbox, Chips et Menu). Le tri, les actions, leurs confirmations et la sélection restent gérés par DataTable. Les autres usages conservent leur présentation par défaut. Le gabarit de catalogue relaie un emplacement facultatif `catalogue-metadata` vers l’en-tête partagé.

Les données proviennent des contrats existants : tâches actives / concurrence maximale, matériel déclaré, dernier signal, outils et encodeurs vérifiés utilisables. La mémoire indiquée est installée ; l’occupation CPU/GPU n’est pas simulée. Une charge indisponible ou un nœud hors ligne ne produit pas de barre d’activité actuelle. Les demandes montrent le code, la capacité demandée et l’échéance, puis ouvrent la vérification et l’approbation existantes. Le compte à rebours est recalculé à chaque rafraîchissement des demandes.

Sur téléphone, les métriques s’empilent dans la même carte. Les actions d’administration restent réservées au rôle admin. Aucun contrat backend, rôle de nœud ni état du rattachement n’est modifié.
