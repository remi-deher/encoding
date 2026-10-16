# Référence d’interface — Ressources et sections pleine largeur

Statut : référence de conception demandée après validation de la présentation de l’onglet **Cluster**. La refonte de **Ce nœud** décrite ici est implémentée (voir [0050](../architecture/0050-local-node-resource-sheet.md)).

Ce document complète [UI_RULES.md](../../UI_RULES.md) et la décision [0049 — Cartes horizontales du cluster](../architecture/0049-cluster-resource-cards.md). Il fixe la direction à suivre pour les prochaines évolutions des vues de gestion et de consultation.

## 1. Principe directeur

**Une ressource occupe la largeur disponible ; son contenu se répartit selon son importance et l’espace disponible.**

La présentation retenue pour les membres du cluster sert de référence : identité et état immédiatement lisibles, métriques rapprochées, capacités secondaires puis accès au détail. La hauteur dépend du contenu.

Une carte représente une ressource complète ou un groupe cohérent d’informations. Chaque propriété n’a pas besoin de son propre cadre. Les détails d’une même installation peuvent se suivre dans des sections ouvertes, avec des titres et des séparateurs légers.

La cohérence vient de la hiérarchie, des espacements, des composants et des interactions. Elle n’impose ni trois colonnes partout, ni un nombre fixe de métriques, ni des cartes de même hauteur.

## 2. Structure d’une page

### En-tête contextuel

- Le titre principal est le nom de l’objet consulté : nom du nœud dans **Ce nœud**, nom du cluster dans **Cluster**.
- Une métadonnée courte précise le contexte utile : autonome, membre de Maison, installation depuis laquelle le cluster est consulté.
- Les actions de la fiche restent dans l’en-tête partagé. Une seule action principale est mise en avant ; les actions secondaires utilisent les variantes Material et le menu commun.
- La navigation utilise la rangée d’onglets du gabarit partagé.
- Le contenu commence ensuite directement. Un bandeau qui répète le nom, l’appartenance et les actions de l’en-tête est inutile.

Un résumé supplémentaire est justifié s’il fournit une décision ou une information distincte. Une alerte, une perte de connexion ou une demande en cours doit rester visible, même sans bandeau de résumé permanent.

### Corps de page

L’ordre général est :

1. Ce qui demande une intervention : incident, demande, changement en attente.
2. L’état et l’activité de la ressource.
3. Les capacités et les réglages utiles à la compréhension.
4. Les informations techniques détaillées, accessibles au besoin.

Les filtres restent fournis par le socle et proches de la liste à laquelle ils s’appliquent. Le nombre d’éléments et les états particuliers peuvent accompagner le titre de section.

## 3. Présentation des ressources

### Une ressource dans une liste

Chaque membre du cluster occupe une ligne visuelle pleine largeur, sous la forme d’une carte souple :

```text
État · Fonctions                                      Menu d’actions
Nom de la ressource
Information secondaire utile

Métrique prioritaire       Capacité ou matériel       Fraîcheur de l’état
Valeur et unité            Valeur et précision        Date ou délai

Capacités, étiquettes ou contexte                     Consulter →
```

Les colonnes intérieures s’adaptent au contenu. Une information absente ne réserve pas artificiellement un emplacement vide. Les longues valeurs peuvent revenir à la ligne.

Une demande de rattachement reprend cette grammaire en donnant la priorité au **code de vérification**, à la capacité annoncée et à l’expiration. Ses capacités sont identifiées comme déclarées par le demandeur. L’action ouvre le parcours existant de vérification avant approbation.

### Une ressource déjà ouverte

La fiche peut utiliser une grande section de synthèse puis des sections ouvertes pleine largeur. Il est inutile de répéter le nom de l’objet en haut de chaque section ou d’encadrer séparément chaque fonction.

Les actions de modification appartiennent à l’en-tête de la fiche. Les informations secondaires restent consultables dans le détail partagé. Une section de lecture ne devient pas un formulaire permanent.

### Densité et décoration

- Employer les jetons de thème existants pour les surfaces, bordures, rayons, couleurs et espacements.
- Utiliser la bordure et l’élévation pour distinguer les ressources ; les sections internes peuvent utiliser un simple séparateur.
- Réserver les chiffres de grande taille aux mesures qui aident à comprendre ou décider.
- Employer les pastilles pour les états, fonctions et capacités courtes. Les longues descriptions restent du texte.
- Réserver la couleur aux états et aux actions utiles. L’état reste toujours écrit.
- Présenter d’abord les capacités pertinentes ; rendre le complément accessible sans multiplier les pastilles.

## 4. Application à « Ce nœud »

### Question de cette vue

**Comment fonctionne cette installation, que peut-elle faire et que dois-je régler ?**

La fiche locale reprend les codes visuels des cartes du cluster, avec une structure adaptée à un seul objet déjà ouvert.

### Composition attendue

```text
NAS principal                              Modifier les fonctions
Membre de Maison                           Consulter les réglages

Ce nœud | Cluster

[Alerte éventuelle ou changement en attente]

État et activité
Agent joignable · Serveur actif · Worker actif
Traitements actifs / limite    Matériel déclaré    Dernier signal

Capacités de traitement
Outils et versions · encodeurs vérifiés · accélérations déclarées
Précisions accessibles dans la fiche de capacités

Identité et appartenance
Nom technique · identifiant du nœud · cluster associé
```

Ce schéma exprime la hiérarchie ; il n’impose pas un cadre autour de chaque section.

- Les fonctions serveur et worker sont deux informations du même nœud, visibles ensemble. Elles ne deviennent pas deux cartes indépendantes.
- Les réglages continuent à utiliser les formulaires et tiroirs existants : fonctions, limites, calendrier, capacités et accès aux bibliothèques.
- Le statut autonome est explicite. Il ne nécessite pas un tableau contenant seulement le nœud local.
- Si le worker est désactivé, l’interface précise que les métriques de traitement ne s’appliquent pas. Les fonctions serveur restent compréhensibles.
- Si l’agent ne répond pas, l’interface distingue une fonction désactivée, un état inconnu et une dernière valeur connue.
- Une demande de changement de fonctions est affichée séparément des fonctions réellement appliquées.
- Les actions Créer, Rejoindre et la gestion des membres restent dans **Cluster** ; **Ce nœud** décrit l’installation locale.

### Ce qui change par rapport à la présentation actuelle

Les cadres indépendants « Rôle du nœud », « Cette installation » et « Capacités de traitement » deviennent une fiche fluide avec une synthèse et des sections cohérentes. Les données et les réglages existants restent accessibles.

## 5. Règles sur les métriques et les états

| Information | Présentation et précaution |
| --- | --- |
| Traitements actifs | Valeur observée / concurrence maximale ; la barre représente l’occupation des places de traitement. |
| Matériel déclaré | Cœurs, mémoire installée, système et architecture. La mémoire installée ne représente pas la mémoire consommée. |
| Dernier signal | Délai ou date avec une fraîcheur cohérente avec le rafraîchissement des données. |
| Encodeurs | Distinguer ceux détectés de ceux vérifiés utilisables. |
| Demande de rattachement | Code à comparer, capacité annoncée et échéance ; aucune réussite déduite d’un délai ou d’une déconnexion. |
| État indisponible | Libellé explicite, sans remplacer une valeur inconnue par zéro. |
| Nœud hors ligne | Dernières informations connues identifiées ; aucune barre présentée comme une occupation actuelle. |

Les graphiques et mesures supplémentaires nécessitent une source réelle, une unité et une utilité. L’occupation CPU/GPU, la mémoire consommée ou le débit d’encodage ne sont pas inventés pour compléter la mise en page.

## 6. Mobile et accessibilité

- Les métriques s’empilent dans la même ressource lorsque la largeur ne permet plus une lecture confortable.
- Les actions restent accessibles, avec des libellés compréhensibles et des zones de clic suffisantes.
- Le contenu ne provoque pas de défilement horizontal de toute la page.
- La hiérarchie des titres reste claire : titre de page, sections, ressources.
- Les états ne reposent pas uniquement sur la couleur. Les contrôles et barres disposent de noms accessibles.
- Les menus, fenêtres, focus et interactions clavier restent portés par Angular Material/CDK et les composants communs.
- Le rafraîchissement conserve la saisie, la sélection et la consultation en cours.

## 7. Mise en œuvre dans le socle

La présentation des membres du cluster utilise `DataTable` en mode `resources`. `ResourceCard` sert aux fiches souples d’une liste de ressources ; `ResourceSheet` compose la fiche pleine largeur d’une ressource déjà ouverte. Ces composants de présentation reçoivent des données et n’effectuent aucun chargement.

Pour une fiche unique comme **Ce nœud**, rechercher d’abord les capacités du gabarit de fiche et des blocs partagés. Si une composition souple manque, la fournir dans le socle avec des emplacements ou des définitions réutilisables. Ne pas ajouter de structure spécifique au nœud dans `ResourceStrip`, ni lui faire charger des API.

Les composants de détail ont des rôles distincts :

- **`ResourceCard`** : identité, état, métriques et actions d’une ressource dans une liste.
- **`ResourceSheet`** : synthèse et sections ouvertes d’une ressource sélectionnée.
- **`DetailCardGrid`** : groupes indépendants de détails catégorisés, repliables au besoin ; ne pas l’utiliser pour donner une bordure à chaque propriété.
- **`DetailFieldList`** : champs de lecture compacts, chronologie ou contenu découpé en onglets.
- **`DataTable`** : comparaison de lignes, tri, sélection et actions ; ses modes `cards` et `resources` changent la présentation de la liste sans changer son rôle de contrôle.

Répartition des responsabilités :

- **Gabarit** : en-tête, navigation, emplacements, états et adaptation générale.
- **Composants communs** : présentation des ressources, métriques, sections, sélection et actions.
- **Définitions métier** : ordre, libellés, champs, métriques et actions disponibles.
- **Contrôleur** : chargement, actualisation, orchestration et erreurs.
- **Serveur** : état de référence et autorisations.

Les listes denses qui nécessitent une comparaison colonne par colonne peuvent conserver le tableau commun. Les éditeurs, formulaires et historiques utilisent leurs gabarits adaptés. Le principe pleine largeur s’applique aux ressources et aux fiches lorsqu’il améliore leur lecture.

## 8. Critères d’acceptation d’une refonte

- Le titre indique l’objet ou le contexte effectivement consulté.
- Les informations prioritaires sont visibles sans ouvrir plusieurs petites cartes.
- Les sections regroupent des informations qui répondent à une même question.
- Les métriques et états affichés sont traçables à des données réelles.
- Les alertes, actions et réglages existants restent accessibles.
- Les interactions sont fournies par le socle partagé et Angular Material/CDK.
- La lecture fonctionne sur ordinateur et téléphone, dans les thèmes existants.
- Les autres usages des composants modifiés sont examinés et les effets signalés.
- Toute limite de données ou de validation est indiquée dans le compte rendu.
