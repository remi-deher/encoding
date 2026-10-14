# 0013 — Catalogue installé et validation backend

## Implémentation

Le registre charge le manifeste déclaratif Base 1.0.0 livré avec l’API. Ses 44 composants reprennent les définitions existantes du frontend, avec les entrées générique et spécialisées, les composants de contrôle et les actions média. Les compatibilités sont normalisées en listes explicites ; les configurations, sorties et options restent celles du catalogue initial.

GET /api/v1/flows/components expose les manifestes installés. POST /api/v1/flows/validate reçoit le document portable et retourne les erreurs structurées (code, path JSON Pointer, message), avec HTTP 422 lorsqu’il est invalide. Les propriétés inconnues et les documents dépassant la limite sont rejetés. La validation n’enregistre aucun flow.

FlowDefinitionValidator est la règle backend commune utilisée aussi avant création d’une exécution. La version annoncée par l’exécution doit correspondre à celle du document. Les tentatives historiques sont conservées ; leur compatibilité devra être recontrôlée par le futur moteur avant attribution ou reprise.

## Invariants contrôlés

Format 1, version positive, identifiant UUID, nom et type média valides ; identifiants uniques de blocs et connexions ; une entrée unique sans liaison entrante ; références exactes de plugin, composant et schéma disponibles ; compatibilité média ; sorties déclarées ; une liaison par sortie ; graphe acyclique ; tous les blocs accessibles depuis l’entrée.

Plusieurs connexions vers une même entrée et plusieurs blocs terminaux sont permis. La validation ne convertit pas ces chemins en parallélisme.

Les valeurs doivent respecter les types text/number/boolean/select, les champs obligatoires, les options compatibles avec le média et les bornes/pas numériques. Les champs inconnus sont rejetés. Les valeurs manquantes ne sont pas complétées silencieusement : le frontend prépare explicitement les valeurs par défaut.

## Interface et transition du catalogue

Le bouton Vérifier le flow utilise le catalogue backend pour obtenir les références de versions, puis demande la validation serveur. Une indisponibilité de l’API ne devient pas une validation réussie. Une réponse concernant un graphe modifié pendant la requête est signalée comme obsolète.

L’adaptateur retire uniquement inputComponentId de la configuration de l’entrée : ce sélecteur d’édition change déjà la référence du composant et n’est pas un paramètre runtime.

L’éditeur charge ses composants depuis ce registre et conserve un catalogue local pour les composants Base nécessaires à l’amorçage et à l’entrée. Les manifestes Plex, Sonarr et Radarr sont intégrés au catalogue serveur ; leurs anciens blocs statiques ont été retirés de l’éditeur. Le manifeste rend une référence disponible pour la validation, sans prouver qu’un worker dispose du processus plugin correspondant. Les composants externes sans manifeste installé échouent explicitement à la validation.

## Limites

Un composant déclaré n’est pas encore exécutable. Le registre ne charge aucun plugin tiers en processus et ne prouve pas la présence des outils d’un worker. Les baux, la réservation des médias, la publication et les sauvegardes de flows restent à implémenter. Aucun média n’est modifié.

La validation est bornée à 1000 blocs, 4000 connexions et un document de 1 Mo. Le contrôle de taille est appliqué au document désérialisé ; une limite HTTP globale devra également protéger les corps avant lecture.

## Vérifications réalisées

Compilation .NET 10 et Angular, lint TypeScript, formatage et démarrage du conteneur. Aucun test automatisé ou parcours d’interaction n’a été ajouté ou exécuté dans cette étape.
