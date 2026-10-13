# 0011 — Gabarit de référence des blocs et paramètres directs

## Design commun

Tous les composants de flow utilisent le même bloc structuré : entrée au centre du bord supérieur (sauf le déclencheur), bandeau avec type à gauche, poignée CDK au centre et plugin/outil à droite, nom centré, description, paramètres directs, puis sorties nommées au bord inférieur.

Les sorties déclarées, erreur comprise, utilisent un pas commun de 104 px lorsque l’espace suffit. Le pas diminue uniformément dans une colonne étroite. Les points correspondent aux extrémités des liaisons SVG. Le libellé d’une sortie ouvre ses actions ; son point sert à créer une liaison. Les étapes non déplaçables présentent un repère désactivé.

## Déclaration des paramètres

Le schéma version 1 gagne deux propriétés compatibles : inline sur chaque champ, suggestions sur un champ numérique. Inline est false par défaut. Les paramètres sont rendus dans l’ordre du schéma, sans limite codée au nombre de champs. Les textes multilignes restent dans le panneau détaillé.

Exemple de configurationSchema :

~~~json
{
  "version": 1,
  "fields": [
    {
      "id": "videoCodec",
      "type": "select",
      "label": "Codec",
      "required": true,
      "inline": true,
      "defaultValue": "hevc",
      "options": [
        { "value": "hevc", "label": "HEVC" },
        { "value": "h264", "label": "H.264" }
      ]
    },
    {
      "id": "quality",
      "type": "number",
      "label": "Qualité CRF",
      "inline": true,
      "defaultValue": 23,
      "min": 0,
      "max": 51,
      "step": 1,
      "suggestions": [18, 20, 23, 26, 28]
    }
  ]
}
~~~

FlowInlineParameters rend les listes, nombres, booléens et textes courts avec Angular Material. Les suggestions numériques servent à l’édition rapide ; elles ne remplacent pas les bornes. Une valeur personnalisée valide reste visible et peut être saisie dans le panneau détaillé. Les options filtrées selon le type de flow utilisent le même schéma que ce panneau.

Les modifications valides mettent à jour uniquement le nœud concerné. Les saisies invalides sont signalées et ne sont pas appliquées. Le panneau retrouve les valeurs modifiées. Les champs ne déclenchent pas l’ouverture du panneau par double-clic ou Entrée.

flowConfigurationFieldError est commune au panneau, aux contrôles directs et à la validation du graphe : valeur requise, type, choix autorisés, nombres finis, bornes et pas. Une absence explicite n’est pas remplacée silencieusement par une valeur par défaut.

## Backend des plugins : prochaine étape

Les définitions sont encore le catalogue frontend de brouillon. Le registre backend devra publier ces schémas validés et versionnés, puis remplacer ce catalogue comme source de référence. Il devra gérer identité, version, compatibilité, activation et capacités, dont base actif par défaut.

Le serveur devra valider les configurations avant enregistrement/exécution et contrôler les composants, versions et sorties. Aucun code Angular, HTML ou JavaScript d’interface ne sera accepté dans une déclaration de plugin.

Un plugin tiers s’exécute dans un processus séparé avec protocole IPC versionné, annulation et délais. Ni ce gabarit ni l’édition directe ne permettent de contourner les accès aux bibliothèques, baux, verrouillages, scans ou validation des sorties.

## Liaisons et fins multiples

Les blocs ont une largeur de référence de 440 px, réduite selon la largeur disponible. Plusieurs sorties distinctes peuvent rejoindre une même entrée ; le bloc affiche leur nombre. Il s’agit de chemins alternatifs, sans barrière de synchronisation implicite.

Chaque sortie libre propose « Terminer ce chemin », qui crée une instance indépendante du composant Terminer le flow. Plusieurs fins peuvent coexister, avec leur propre configuration. Une sortie reliée doit être déconnectée avant de créer une fin à cet endroit.

Les liaisons SVG utilisent un graphe de visibilité orthogonal calculé à partir des dimensions réelles des blocs, avec des angles arrondis. Elles contournent les cartes visibles et sont recalculées au changement de disposition. Les branches repliées ne dessinent pas leurs liaisons internes.

## Actions sur les blocs et les liaisons

Un bouton + accessible au clavier est positionné au milieu de chaque liaison SVG visible. Il ouvre le catalogue pour insérer une étape sur cette seule connexion, en conservant les autres entrées éventuelles de la cible. Les ajouts textuels redondants sur les chemins reliés sont retirés ; le menu des sorties reste disponible, notamment pour les chemins libres et les liaisons masquées.

La corbeille du bloc appelle la suppression existante : une étape simple peut être contournée, les enfants d’un embranchement restent conservés, et l’entrée ne peut pas être supprimée.

Le composant PageHeader possède une option shellTitle pour transmettre un titre dynamique au header global. Le titre est associé à l’adresse courante et libéré à la destruction de la page. L’éditeur fournit le nom du flow ; les autres pages conservent leur titre de route.
