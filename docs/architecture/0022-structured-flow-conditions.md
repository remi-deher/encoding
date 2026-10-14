# 0022 — Conditions structurées dans les flows

## Décision

Le plugin intégré `conditions` est installé et actif par défaut. Son bloc `conditions-match` fournit les sorties Oui, Non et Erreur. Un même composant couvre les règles simples, les groupes ET / OU et les groupes imbriqués. Des préréglages modifiables facilitent les critères courants. Le bloc JavaScript historique de Base conserve sa référence et son comportement.

## Contrat et stockage

Le champ déclaratif `condition-tree` utilise un arbre JSON versionné par le schéma du composant : un groupe `{ kind: "group", match: "all" | "any", children: [...] }` contient d'autres groupes et des règles `{ kind: "rule", field, operator, value?, variableName?, scope? }`. L'arbre est stocké avec les valeurs de l'instance de bloc dans le document du flow, ses versions PostgreSQL, ses sauvegardes et ses exports existants. Aucune migration de base n'est nécessaire.

Le catalogue de champs, leurs types, opérateurs, compatibilités média et préréglages fait autorité dans `Encoding.Contracts`. L'API l'expose dans le schéma, le formulaire commun le rend et le runtime utilise la même définition. Un plugin ne fournit pas de code d'interface. Ce nouveau champ commun est utilisable par les schémas de composants et ne modifie pas le rendu des champs simples existants.

La validation refuse les champs et propriétés inconnus, les opérateurs incompatibles, les valeurs du mauvais type, les instantanés non autorisés, les groupes vides lors de la publication, plus de 100 éléments ou plus de 8 niveaux. Un brouillon peut conserver un groupe vide ou une règle incomplète ; il ne peut pas être exécuté avant validation complète.

## Évaluation native

Les règles utilisent les métadonnées ffprobe courantes ou d'origine, les variables et le contexte durable de l'exécution : bibliothèque d'origine, nœud d'accès d'origine, worker courant, chemin relatif d'origine, extension courante, état de transformation et plan en attente. Les critères de codec ou de nombre de canaux désignent explicitement la première piste du type correspondant. Les critères de langue ou de codec audio portent sur les listes de pistes. Les codes de langue sont ceux du média (par exemple `fra`). Les textes ignorent la casse ; les nombres et booléens restent typés.

Le runtime demande les scans obligatoires quand un critère média utilise des métadonnées qui ne sont pas encore disponibles. Les règles de variables, d'origine et d'état ne téléchargent pas le média pour être évaluées. Les checkpoints et relais existants conservent ces données, et le bail expose aussi l'identifiant de bibliothèque.

Un champ absent provoque la sortie Erreur lorsqu'une comparaison le demande. Les opérateurs `exists` / `missing` permettent de traiter explicitement cette absence. Les groupes suivent le court-circuit usuel : ET s'arrête au premier Non, OU au premier Oui ; cela permet de protéger une comparaison facultative avec une règle de présence. Une variable du mauvais type provoque aussi Erreur, sans conversion implicite. Une absence de pistes est une observation valide : présence vaut Non et nombre vaut zéro. Une langue non renseignée reste une donnée absente.

Le bloc est idempotent et ne modifie aucun fichier. Il n'exige ni JavaScript, ni processus plugin externe. Seuls les agents annonçant `plugin/conditions` 1.0.0 peuvent le recevoir. Les garanties de scans, d'accès, de bail et de publication du moteur restent applicables.

## Interface et vérifications

Le champ commun fournit sélection de champ et d'opérateur, valeurs typées, choix de l'instantané, groupes imbriqués, ajout, suppression et réordonnancement. Les critères et préréglages sont filtrés par type de flow. Le bloc affiche un résumé des règles ; son éditeur reste le formulaire commun du drawer.

Les tests couvrent validation et comparaison native, structures imbriquées, types, données absentes, limites, compatibilité des agents et sélection des champs. Les tests PostgreSQL du runner vérifient les trois branches avant scan et leur relais, puis une condition comparant H.265 courant et H.264 d'origine après conversion réelle. L'interface a été vérifiée manuellement à 1280 et 390 pixels : choix d'un préréglage, ajout d'un groupe OU, édition d'une variable numérique, application, validation serveur, enregistrement de brouillon, rechargement et publication. Le JSON enregistré conserve le groupe imbriqué et le type numérique ; la sauvegarde publiée est disponible.
