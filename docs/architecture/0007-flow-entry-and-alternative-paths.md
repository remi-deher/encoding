# 0007 — Entrée média et chemins alternatifs

## Décision

Chaque définition comporte exactement une entrée. L’éditeur initialise une entrée Fichier générique pour un nouveau flow, et conserve une entrée spécialisée pour ses exemples préconfigurés. La configuration du bloc permet de choisir Fichier générique ou l’entrée spécialisée adaptée au type déclaré, sans modifier son identité, son nom, sa position ou ses connexions. Ce bloc ne peut pas être supprimé ni recevoir de connexion entrante.

L’entrée spécialisée déclenche le scan immédiatement. L’entrée générique permet des scripts préparatoires avec les seules informations du fichier ; le moteur impose ensuite le scan avant la première opération qui requiert les métadonnées ou modifie le média. Un échec du scan interdit cette opération. Les instantanés avant traitement restent immuables.

Les composants déclarent leur compatibilité avec les types de flow. Par défaut, les blocs communs sont accessibles partout, les opérations audio dans les flows musique et vidéo, les opérations sous-titres dans les flows sous-titres et vidéo. Les contraintes d’outil peuvent être plus strictes : les opérations MKVToolNix s’appliquent au conteneur vidéo, et ne sont pas proposées pour un fichier de sous-titres autonome. Les champs de sélection peuvent aussi restreindre leurs options selon le média (par exemple le choix MKVToolNix du préparateur reste limité à la vidéo).

Une sortie peut rejoindre au plus un nœud. Plusieurs sorties de chemins alternatifs peuvent rejoindre le même port d’entrée, sans exécution parallèle ni attente de toutes les branches. Les cycles et auto-connexions restent interdits. Une sortie Erreur rejoint une action de secours ; une erreur non traitée échoue. Les nœuds terminaux ne possèdent pas de sorties.

## Implémentation actuelle

Les entrées et les composants Script, Préparer le traitement, Exécuter le traitement, Reporter et Terminer sont disponibles dans l’éditeur. Les scripts possèdent un langage, un code et une durée maximale ; les contrôles disposent de paramètres déclaratifs. Ces paramètres expriment une intention de configuration, pas un contrat de scripting exécuté par le worker.

Les actions média compatibles proposent Ajouter au plan ou Opération autonome. Le mode plan est la valeur par défaut pour les transformations regroupables. Préparer initialise le plan ; Exécuter le traitement le consomme. Le futur moteur doit vérifier qu’un plan compatible existe sur chaque chemin, préserver les pistes inchangées, gérer explicitement les sorties ignorées et vérifier les capacités du worker avant l’exécution. Des branches alternatives héritent du contexte du chemin effectivement suivi, sans fusion implicite de variables.

La validation de connexions est centralisée dans `flow-graph.ts` et utilisée par le canevas et par la page. Les positions manuelles restent conservées lors des ajouts/suppressions ; Organiser le flow relance Dagre explicitement. Les noms d’instances sont séparés du nom du type et de la configuration. Appliquer au flow modifie le brouillon en mémoire.

## Garanties de l’exécution à venir

La validation backend doit appliquer les mêmes invariants à toute définition enregistrée ou importée. Un bloc visible ne peut pas rendre le scan optionnel. Scripts et plugins accèdent aux médias et à la publication par les mécanismes partagés ; aucun script n’est autorisé à contourner les baux, les verrouillages et la validation des sorties.

Le report doit persister une prochaine date de tentative et libérer la capacité du worker. Les scripts doivent s’exécuter hors du processus hôte avec annulation et durée bornée. Le scan, l’historique, le stockage JSON, les baux et l’exécution des scripts/outils ne sont pas encore implémentés. Cette évolution de l’éditeur prépare ces contrats sans prétendre les exécuter.
