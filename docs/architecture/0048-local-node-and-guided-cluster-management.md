# 0048 — Ce nœud et gestion guidée du cluster

Statut : implémentée, après validation de la maquette interactive.

## Décision

La page Nœuds expose deux onglets : **Ce nœud** pour les fonctions, l’identité et les capacités locales ; **Cluster** pour l’appartenance, les membres et les demandes de rattachement. Un nœud autonome présente une fiche et les actions Créer / Rejoindre, sans tableau de membres ni filtres de rôles.

La création réutilise le formulaire et sa confirmation partagés. Le rattachement utilise un gabarit partagé `GuidedSetup`, fondé sur le stepper Angular Material/CDK : choix d’un cluster détecté ou adresse manuelle, vérification de son identité, confirmation explicite de la demande, attente d’approbation puis résultat observé. Vérifier une adresse ne demande pas le rattachement. La saisie reste stable pendant l’actualisation des états.

Le contrôleur `LocalNodeController` orchestre les API authentifiées existantes. Les présentations, champs et étapes sont déclaratifs. Le navigateur ne contacte jamais directement l’agent. Aucun contrat backend ni mécanisme de jumelage n’est modifié.

Une demande en cours est retrouvée depuis l’état de l’agent après fermeture ou rechargement. L’expiration ou l’annulation restitue l’état précédent ; la clé de jumelage et une déconnexion ne constituent jamais une confirmation de réussite. Le parcours indique qu’après approbation le serveur local peut s’arrêter, et que la configuration locale n’est pas importée automatiquement.

Le cluster conserve ses demandes reçues, les méthodes avancées d’ajout, les réglages des membres et les actions groupées. Les membres utilisent le tableau partagé sur grand écran et sa présentation en cartes sur mobile. Les actions groupées utilisent la sélection et la confirmation communes du tableau.

## Impacts sur le socle

- `GuidedSetup` est un composant générique sans route ni API métier, réutilisant les formulaires, tableaux et fiches communs.
- Les identifiants ARIA de `DetailCardGrid` sont uniques par instance, y compris lorsqu’une fiche et un tiroir affichent les mêmes cartes. Ses autres usages conservent leur présentation.
- L’ancienne route `/worker` redirige vers `/nodes`, soumise à la session habituelle. La console directe et le panneau local remplacés sont retirés ; la coque ne conserve plus leur navigation particulière.

Cette décision remplace la présentation du panneau local de 0044 et la disposition de la page décrite dans 0046 / 0047. Les garanties de rattachement, de révocation et de retour à l’état précédent restent celles des architectures correspondantes.
