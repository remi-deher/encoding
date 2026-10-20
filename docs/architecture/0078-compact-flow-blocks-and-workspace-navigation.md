# 0078 — Blocs compacts et navigation des flows complexes

## Décision

Les vues Guidé et Canvas conservent le graphe et les mutations communes de 0077. Elles partagent les styles de titre, de résumé et de commandes des blocs ; les actions restent dans le flux de mise en page, y compris pour les titres longs et les étapes terminales. Le type et le fournisseur restent visibles. Les paramètres scalaires déclarés inline, les conditions décrites par le catalogue et le nom du profil fournissent le résumé ; les scripts et secrets ne sont jamais affichés dans ce résumé.

Guidé utilise des blocs de référence de 340 px, élargis pour les sorties déclarées. Les branches imbriquées occupent leur largeur intrinsèque, sans multiplier la largeur des branches voisines par la profondeur du graphe. Le défilement reste local au workspace. Les chemins ont un libellé, un nombre d’étapes et une commande de repli ; les suites communes désignent leur cible et rappellent l’exécution séquentielle des alternatives. Les renvois explicites restent présents pour les graphes non structurables.

Canvas utilise également des entrées supérieures et sorties inférieures nommées. Les liaisons reprennent le routeur orthogonal partagé et leurs points d’attache sont mesurés sur les ports rendus, y compris pendant un déplacement. Les titres et la commande de configuration ouvrent l’inspecteur existant. Les commandes de navigation vivent dans le workspace pour préserver la lisibilité de l’en-tête de page.

Ctrl + molette zoome autour du pointeur uniquement sur Canvas. Le glisser sur le fond ou le bouton central déplace la vue. « Tout afficher » cadre les blocs réels, indépendamment de la surface extensible. Une minicarte permet de naviguer, avec un bouton accessible au clavier.

La disposition automatique est explicite : projection verticale déterministe par rang topologique, espacement selon les dimensions réelles, préservation des brouillons détachés ou cycliques. La disposition précédente est restaurable par « Annuler la disposition ». Positions et annulation de disposition restent des préférences locales au navigateur, distinctes de l’historique métier. Aucun raccordement, réglage, version portable ou état d’exécution n’est modifié.

L’inspecteur réutilise le formulaire de schéma commun. Les conditions, références de scripts, code et choix de profils/encodeurs sont des réglages courants même sans indicateur inline. Les autres champs facultatifs restent disponibles dans les réglages avancés, ouverts lorsqu’un champ visible y est invalide. Les autres usages du formulaire conservent le rendu complet par défaut.

## Impact et vérification

Le changement porte sur le socle partagé de graphe, la projection Canvas et le formulaire déclaratif existants. Les consommateurs de la projection Canvas utilisent désormais un placement vertical ; la définition stockée n’a pas changé. Aucun nouveau composant de traitement ni capacité média n’est inventé.

Les tests de modèle couvrent la non-mutation, les convergences, les blocs de dimensions différentes et les brouillons invalides. Les tests Angular couvrent la visibilité des conditions et des erreurs avancées. Les E2E de l’éditeur sur ordinateur et mobile couvrent les branches imbriquées, trois sorties exclusives, plusieurs fins, la configuration, la disposition annulable, le zoom, l’accessibilité et le confinement du défilement. Les cas média des fixtures sont illustratifs et ne déclenchent aucun outil réel.
