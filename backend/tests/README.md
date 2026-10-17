# Vérifications du runtime

La suite utilise les vrais dépôts, des fichiers SQLite temporaires, les endpoints HTTP du runtime et le runner worker. Elle ne dépend d'aucun serveur de base externe ; chaque scénario supprime son fichier et ses médias synthétiques à la fin.

Avec .NET 10 :

```powershell
dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release
```

La suite utilise xUnit (`dotnet test`). Les scénarios de base s’exécutent toujours sur SQLite. FFmpeg, ffprobe et MediaInfo sont nécessaires aux scénarios média ; `backend/tests/Dockerfile` fournit le SDK et ces outils. La CI exécute toute la suite, exige les outils média, collecte la couverture et conserve les résultats `.trx` en artefact.

Depuis la racine du dépôt, avec Docker :

```powershell
docker build -t encoding-runtime-tests -f backend/tests/Dockerfile backend/tests
docker run --rm -v "${PWD}:/source" -w /source encoding-runtime-tests dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release
```

Les scénarios média encodent une vidéo synthétique de 1 seconde en H.265 : une fois avant le relais, et une fois en transférant le plan avant son exécution. Le service source publie réellement le résultat distinct et les tests vérifient que la source reste intacte.

La suite couvre aussi les conditions structurées : groupes ET / OU, types et données absentes, limites de taille/profondeur, instantanés d'origine/courant, disponibilité du plugin et trois branches du runner avant téléchargement. Le parcours média vérifie une condition H.265 courant ET H.264 d'origine après conversion.

Le testeur de flow est couvert sans lancer de traitement : branches et reconvergence, probes/variables, résultats fictifs de transformation, conservation de l’origine, relais avec paramètres hérités, plan non exécuté, contrats HTTP et erreurs de profil. Un fichier synthétique vérifie MediaInfo et l’intégrité de la source ; saturation et annulation vérifient la libération des ressources.

La suite vérifie également les publications inchangées et concurrentes, l’absence de nouvelles sauvegardes, la concurrence des brouillons, les modifications réelles, la version exacte retournée par l’API et la conservation des versions identiques lors d’un import d’archives.

Les tests de remplacement de source (fichiers, politique, période de grâce, restauration) et de perte d’un worker couvrent les règles de l’architecture 0030. Le remplacement réel d’une source H.265 puis sa restauration sont aussi vérifiés avec FFmpeg. Les scénarios de maillage utilisent deux bases SQLite indépendantes et vérifient la convergence sans quorum.

## Frontend

`npm test` (dans `frontend/`) exécute les tests de logique pure (`node --test`) puis les tests de composants Angular (Vitest, `*.spec.ts`, via `ng test`). `npm run test:e2e` lance le parcours E2E léger Playwright (bureau et mobile, API simulée, vérifications d’accessibilité axe) ; il ne requiert ni backend ni média. En CI, les rapports et traces sont conservés en cas d’échec. Exception temporaire documentée : la règle axe `color-contrast` est désactivée dans `e2e/smoke.spec.ts` tant que les jetons de thème de la vue d’ensemble ne sont pas corrigés (12 éléments signalés).

Les attentes durables (0033) sont vérifiées sur SQLite : libération du worker et réservation conservée, reprise à l'étape suivante, délai maximal avec et sans sortie « Délai dépassé », évaluations concurrentes, événements serveur (charge, état d'un worker), sondes de plugin protégées par l'époque, runner réel et annulation.

## Plugins et déclencheur d’import

`backend/tests/Encoding.Plugins.Tests` vérifie la correspondance des chemins, l’analyse des webhooks Sonarr/Radarr, l’acceptation des manifestes par le catalogue, l’exécution du plugin Sonarr par `PluginProcessHost` dans le bac à sable (ignorée si Landlock ou python3 est absent) et le déclencheur sur une base SQLite temporaire :

```powershell
dotnet run --project backend/tests/Encoding.Plugins.Tests --configuration Release
python3 -m unittest discover -s backend/plugins/tests -v
```

Les tests Python lancent le SDK, le plugin d’exemple et les plugins Sonarr/Radarr comme processus réels contre un faux service HTTP local ; ils n’utilisent aucun service personnel.
