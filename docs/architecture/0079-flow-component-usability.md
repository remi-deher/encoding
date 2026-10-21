# 0079 — Configuration compréhensible des composants de flow

## Décision

Le schéma partagé distingue `importance` (`basic`, par défaut, ou `advanced`)
de `inline` (résumé sur le bloc) et de `required` (validation). `section`
regroupe les champs. Les conditions de visibilité ne suppriment aucune valeur :
un changement de mode reste réversible et ne modifie pas un flow publié.
Le formulaire partagé est aussi utilisé pour les configurations de plugins ;
les champs sans métadonnées restent visibles. Les secrets et scripts ne figurent
jamais dans le résumé des paramètres.

Les sélecteurs, les opérations ciblées et les préréglages doivent réutiliser les
contrats déclaratifs et les traitements communs. Aucun plugin ne livre d'interface
Angular. Les changements de comportement sont explicites et les définitions
historiques conservent leurs options et valeurs implicites.

## Livraison

1. Formulaire : importance, sections, visibilité et résumé des valeurs effectives.
2. Sélecteurs partagés : contexte observé, références conservées si indisponibles.
3. Responsabilités : opérations ciblées et présentation des blocs composites.
4. Options : redimensionnement conditionnel, normalisation, audio et sous-titres.
5. Compatibilité : régression des définitions historiques et documentation.

Chaque lot est vérifié et committé séparément. Les garanties de scan, de bail,
de plan média, de validation et de publication restent celles du moteur.

## Opérations ciblées

Subtitles 1.1.0 ajoute sept opérations ciblées. FlowOperationConfiguration impose les effets autorisés avant l'appel aux planificateurs communs ; un paramètre hors schéma ne peut pas activer un autre effet. Les anciens identifiants gardent leurs valeurs implicites. Le composant complet reste disponible pour une écriture groupée. Les nouveaux composants exigent un worker annonçant la nouvelle version du plugin.

Les métadonnées editor restent compatibles avec les valeurs textuelles existantes. Les choix observés ne remplacent jamais une référence inconnue. Les chemins de travail et noms de jeux de polices restent saisissables : leur présence effective dépend du worker et est vérifiée lors du traitement.


## Options de traitement

Le champ record-list est une liste bornée d’objets à champs primitifs déclaratifs. Le backend valide les lignes et rejette les clés inconnues. La première règle audio correspondante décide ; aucune correspondance conserve la piste. Les étapes résultantes réutilisent le plan FFmpeg et les contrôles de disposition/LFE.

Le redimensionnement conserve son comportement historique par défaut ; les résolutions prédéfinies et la réduction sans agrandissement sont explicites. La normalisation conserve le mode personnalisé et une passe par défaut ; deux passes analysent la source avant application des mesures finies. Les styles ASS personnalisés sont des instantanés de paramètres inclus dans la version du flow ; les compositions complexes restent préservées.


## Compatibilité et ressources (étape 5)

Versions minimales : un composant ou une option peut exiger une version minimale de plugin ; l'éligibilité refuse un worker plus ancien, y compris via les anciens alias Base, tandis qu'un flow historique reste exécutable par un worker ancien compatible.

Polices : l'inventaire des jeux de polices d'un nœud est facultatif et borné. Seuls les dossiers contenant une police TTF/OTF/TTC non symbolique sont annoncés. Les suggestions viennent des workers observés ; l'aperçu, l'attribution et la reprise vérifient le jeu demandé sur le worker considéré. Un inventaire absent, vide ou différent rend ce worker inéligible pour ce segment. Les anciens workers doivent publier un inventaire après mise à jour avant de recevoir un bloc d'ajout de polices ; les noms de fichiers demandés et la présence effective restent contrôlés au traitement.

Fichiers produits : `artifact:<identifiant-encodé>/<indice>` désigne un fichier additionnel d'un producteur précédent (capacité worker `artifact-references`). Le préfixe contient un caractère interdit dans les anciens chemins de bibliothèque : un chemin valide auparavant n'est jamais réinterprété.

Stabilisation : une piste audio reçoit un codec explicite et une chaîne de filtres ordonnée dans le plan FFmpeg. La normalisation conserve le codec de conversion demandé ; deux passes analysent la même chaîne préalable (rééchantillonnage et disposition compris). Les indices de sortie suivent le mapping lorsque les pistes non traitées sont retirées. Une fréquence explicitement demandée participe à la conformité audio ; les anciens réglages implicites restent conservés, et FLAC ne dépend pas d'un débit de compression avec perte.

La conversion ciblée SRT → ASS fonctionne avec un style « aucun » : elle conserve alors l'en-tête converti. Les résumés des règles exposent les restrictions avancées actives. Les champs inline et l'inspecteur partagent la visibilité conditionnelle ; masquer un champ conserve sa valeur. Les références affichent le nom observé tout en stockant l'identité.

Les tests utilisent de petits médias synthétiques pour couvrir conversion + normalisation, filtres multiples, indices remappés, extraction → relais → consommation sur deux workers et branche sans production. Une référence absente ou corrompue échoue explicitement, sans être demandée comme fichier de bibliothèque ni publiée.
