# Règles de programmation de l’interface

## 1. Partir du besoin utilisateur

Avant de coder une page, identifier la question à laquelle elle répond.
Le besoin détermine le gabarit, puis la présentation.

Chaque page ou section répond à un seul besoin :
- Surveiller : est-ce que tout va bien ?
- Traiter : que dois-je faire maintenant ?
- Gérer : comment maintenir ces ressources ?
- Suivre : où en sont les opérations en cours ?
- Parcourir : qu’y a-t-il d’intéressant ?
- Explorer : comment trouver précisément un élément ?
- Analyser : que montrent les données et leur évolution ?
- Créer : comment mettre quelque chose en place ?
- Choisir : quel candidat sélectionner ?
- Anticiper : qu’est-ce qui arrive, et quand ?
- Comprendre : que s’est-il passé ?
- Configurer : comment régler le fonctionnement ?
- Fiche : tout connaître sur un élément.

Une page qui mélange plusieurs besoins se découpe en sections,
onglets ou pages distinctes.

## 2. Toutes les pages utilisent des gabarits communs

Aucune page ne construit sa propre structure.

Un socle commun porte :
- la navigation ;
- la recherche et les filtres ;
- les outils de page ;
- les états chargement, erreur, vide et non configuré ;
- les espacements et le comportement mobile.

Le gabarit métier fixe la structure, l’ordre et les interactions.
La page fournit les données, les définitions et le contenu prévu
dans les emplacements du gabarit.

Si une capacité manque, faire évoluer le gabarit ou son bloc commun
pour toutes les pages. Ne jamais le contourner depuis une page.
Vérifier ses usages et signaler cette évolution.

## 3. Utiliser exclusivement les composants communs

Réutiliser les composants UI et les frameworks du projet.
Ne pas réimplémenter leurs comportements.

Avant de créer un composant :
1. Chercher un composant existant.
2. Étendre ce composant si une capacité lui manque.
3. Créer un nouveau composant seulement s’il est réutilisable.

Un composant commun possède :
- un nom générique ;
- des propriétés décrivant un besoin ;
- aucune donnée ni route propre à une page ;
- une place dans le dossier des composants partagés.

Ne pas créer un composant de présentation pour une seule page.

## 4. Séparer les responsabilités

- Vues : assemblage des gabarits et du contenu.
- Gabarits : structure et interactions communes.
- Composants UI : affichage et comportements réutilisables.
- Composables : lecture des données, état et orchestration des actions.
- Définitions métier : colonnes, filtres, actions, confirmations,
  étapes et champs des formulaires.
- Serveur : règles métier et état de référence.

Une vue ne contient pas de logique réutilisable.
Les gabarits et composants d’affichage ne chargent pas eux-mêmes
les données métier.

Suivre les conventions du code environnant.

## 5. Décrire les interfaces par des données

Les colonnes, filtres, actions et formulaires sont des définitions,
rendues par les composants communs.

Les filtres actifs et leurs pastilles se déduisent des mêmes
définitions : ne pas maintenir deux descriptions séparées.

Les créations et saisies utilisent un gabarit commun piloté par :
- des étapes ;
- des champs typés ;
- des validations ;
- des conditions ;
- des tests de connexion si nécessaire ;
- un récapitulatif modifiable ;
- une soumission et un résultat.

Ne pas écrire de formulaire de création directement dans une page.
Ajouter tout nouveau type de champ au gabarit commun.

## 6. Unifier navigation, recherche et filtres

Une seule rangée de navigation par page, fournie par le socle.

Distinguer trois rôles :
- Navigation : change de vue et d’adresse.
- Consultation : affiche une partie d’un élément.
- Filtrage : restreint une liste.

Ne pas utiliser ces composants de manière interchangeable.

La recherche et les filtres passent toujours par le socle.
Les filtres s’appliquent immédiatement et se retirent par leurs
pastilles. Ne pas ajouter de barre de recherche ou de panneau
de filtres propre à une page.

Les contrôles qui cadrent toute la page — période, vue du calendrier,
pause d’un service — passent par les outils de page.
Les filtres d’une liste restent près de cette liste.

## 7. Centraliser les tableaux et leurs actions

Un tableau de lecture commun gère tri, sélection et adaptation mobile.

Un tableau de contrôle commun ajoute :
- les actions groupées ;
- les actions de ligne ;
- les actions sur toute la liste ;
- les confirmations ;
- l’ouverture d’un élément ;
- le chargement supplémentaire.

Ne pas écrire de barre groupée, menu de ligne ou confirmation
à côté du tableau.

Un ensemble de cellules contenant des champs à remplir relève
d’un formulaire de configuration, pas d’un tableau de données.

## 8. Toutes les fenêtres utilisent un même socle

Le socle commun gère focus, clavier, fermeture, titre, erreur,
actions et adaptation mobile.

Choisir le contenu selon le rôle :
- Confirmer : confirmation courte avec deux boutons.
- Saisir : gabarit de création piloté par une définition.
- Gérer une liste : tableau de contrôle dans la fenêtre.
- Consulter : contenu de lecture sans champs.
- Choisir : candidats et sélection.

Utiliser les tailles standard du socle.
Ne pas inventer une largeur ou un mécanisme de fenêtre par page.

## 9. Transmettre les objets métier entiers

Définir des contrats métier communs entre serveur et interface.

Transmettre l’objet complet aux composants plutôt que recopier
ses champs d’affichage dans chaque page.

Centraliser la traduction d’un état métier en présentation.
Ne pas déduire artificiellement un état certain depuis une mesure :
une progression à 100 % ne prouve pas la fin d’une opération.

Distinguer état observé, état souhaité et absence d’information.

## 10. Placer les actions selon le besoin

- Fiche : actions dans l’en-tête, jamais dans les onglets.
- Explorer : ouvrir la fiche ; les actions vivent dans la fiche.
- Parcourir : découvrir ; les rails mènent à Explorer déjà filtré.
- Traiter : actions unitaires et groupées, retour immédiat,
  retrait de l’élément traité et annulation si possible.
- Comprendre : aucune action dans la chronologie ;
  au plus une dans le détail.
- Configurer : un seul enregistrement global avec Annuler
  et protection contre la perte de modifications.
- Suivre : progression, cause du blocage et actions pour débloquer.

Les retours de réussite ou d’échec passent par le système
de notifications commun.

## 11. Préserver la cohérence et vérifier les changements

Toute modification d’un composant commun peut affecter plusieurs pages :
examiner ses usages avant de le modifier.

Vérifier selon la portée du changement :
- types et compilation ;
- tests métier et interactions ;
- comportement sur ordinateur et téléphone ;
- absence de régression fonctionnelle.

Supprimer les composants et styles devenus inutiles après avoir
vérifié qu’ils ne sont plus utilisés.

Signaler explicitement les fonctions supprimées, les changements
de comportement et les limites de validation.

En cas de doute sur le besoin métier, demander plutôt qu’inventer.