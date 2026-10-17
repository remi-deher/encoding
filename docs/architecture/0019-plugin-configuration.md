# 0019 — Configuration globale des plugins et dérogations par flow

## Décision

La configuration des plugins possède deux niveaux :

1. **Configuration du plugin** : valeurs par défaut partagées par le cluster, gérées dans la section Plugins. Elles servent à tous les flows qui utilisent ce plugin.
2. **Dérogation du flow** : valeurs enregistrées séparément avec une révision propre au couple flow/plugin. Elles ne s’appliquent qu’à ce flow et remplacent les valeurs globales correspondantes. Modifier la configuration globale ne réécrit pas les dérogations déjà enregistrées. Laisser un champ vide ou réinitialiser les dérogations rétablit l’héritage de la valeur globale.

La configuration propre à une instance de composant reste distincte. Elle règle l’action de ce bloc (par exemple un identifiant de série ou de bibliothèque) et ne change pas la connexion partagée du plugin ni les dérogations du flow.

Pour chaque invocation, le moteur fournit séparément les paramètres de l’instance et la configuration effective du plugin. Une valeur de dérogation remplace la valeur globale correspondante ; les autres valeurs globales restent héritées. Les schémas de configuration du plugin et des composants sont déclaratifs, versionnés et rendus par les formulaires communs ; les plugins ne fournissent pas de code d’interface.

## Secrets et archives

Les clés API, mots de passe et autres champs secrets ne sont jamais enregistrés en clair dans une définition de flow, une sauvegarde JSON, une archive portable, un événement ou un journal. Les configurations globales et les dérogations de flow sont conservées chiffrées en base. Pendant la réconciliation, Encoding les déchiffre localement, les protège avec AES-GCM et la clé de données partagée du cluster, puis les rechiffre avec le trousseau Data Protection local du serveur destinataire. Le runtime ne transmet les valeurs effectives qu’au worker auquel la tâche a été attribuée et au processus plugin concerné.

La configuration globale et les dérogations de flow ne sont pas incluses dans les exports JSON/ZIP : elles appartiennent au cluster et sont configurées séparément. Après import dans un autre cluster, le flow hérite donc des réglages globaux de ce cluster jusqu’à la création de nouvelles dérogations.

## Cohérence et validation

La révision envoyée par le formulaire reste informative : une valeur périmée n’empêche pas l’écriture locale. La fusion des champs (dont la conservation des secrets laissés vides) est sérialisée dans une transaction. Entre bases locales, la dernière écriture observée gagne au retour du maillage selon la règle LWW de 0049. À l’attribution d’une tâche, le serveur fusionne les défauts globaux et les dérogations du flow puis transmet cet instantané au worker. Les modifications n’altèrent pas une tâche déjà attribuée ; elles s’appliquent aux attributions suivantes. Le backend valide les valeurs selon le schéma du manifeste lors de l’enregistrement.

Les configurations globales et les dérogations de flow sont réconciliées par le journal de 0049. Une partition peut donc laisser deux valeurs locales temporairement différentes ; la règle LWW choisit celle qui converge après reconnexion.

Le contrat d’invocation transporte les paramètres du composant et la configuration effective du plugin dans des propriétés séparées. La disponibilité d’un manifeste ne signifie toujours pas qu’un processus plugin exécutable est installé.

## Portée

La section Plugins permet de modifier les valeurs globales et de tester la connexion au service externe. Le test utilise les champs affichés ; un secret laissé vide est repris depuis la configuration chiffrée. Il ne sauvegarde pas les valeurs saisies. La requête part du serveur Encoding et n’est pas une preuve que les workers peuvent joindre le service.

La création et la réinitialisation des dérogations du flow restent exposées par l’API. Le panneau « Paramètres des plugins » a été retiré de l’éditeur ; les réglages globaux se consultent dans la section Plugins et les paramètres d’instance sur les blocs. Les configurations utilisent les révisions du registre et le contrat d’invocation sépare les paramètres du composant des paramètres effectifs du plugin. L’exécutable du plugin reste une capacité distincte de sa configuration.
