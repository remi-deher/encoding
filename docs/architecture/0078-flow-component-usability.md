# 0078 — Configuration compréhensible des composants de flow

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
