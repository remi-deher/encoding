# Vérifications du runtime

La suite utilise les vrais dépôts, PostgreSQL, les endpoints HTTP du runtime et le runner worker. Elle ne dépend d'aucun service personnel. Les données sont créées dans un schéma PostgreSQL unique `handoff_<uuid>` puis supprimées, et les fichiers synthétiques restent dans un dossier temporaire propre à la suite.

Avec .NET 10 :

```powershell
$env:ENCODING_TEST_DATABASE = 'Host=localhost;Database=encoding_test;Username=encoding;Password=encoding'
dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release
```

La suite utilise xUnit (`dotnet test`). Sans cette variable, les scénarios PostgreSQL sont ignorés localement ; les contrats, les règles de sélection, la compatibilité et le testeur de flow (dont l’import synthétique MediaInfo) sont vérifiés. FFmpeg, ffprobe et MediaInfo sont nécessaires. `backend/tests/Dockerfile` fournit le SDK et ces outils. La CI fournit PostgreSQL, exécute la suite complète avec `ENCODING_TEST_REQUIRE_DATABASE=1` et `ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1` (aucun scénario ne peut être ignoré en silence), collecte la couverture (indicateur, pas objectif) et conserve les résultats `.trx` en artefact.

Depuis la racine du dépôt, avec Docker et une base de test éphémère :

```powershell
docker build -t encoding-runtime-tests -f backend/tests/Dockerfile backend/tests
docker run --rm -d --name encoding-runtime-test-db -e POSTGRES_USER=encoding -e POSTGRES_PASSWORD=encoding_test -e POSTGRES_DB=encoding_test postgres:17
docker exec encoding-runtime-test-db pg_isready -U encoding -d encoding_test
# Attendre que pg_isready annonce une base disponible.
docker run --rm --network container:encoding-runtime-test-db -e 'ENCODING_TEST_DATABASE=Host=127.0.0.1;Database=encoding_test;Username=encoding;Password=encoding_test' -v "${PWD}:/source" -w /source encoding-runtime-tests dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release
docker stop encoding-runtime-test-db
```

Les scénarios média encodent une vidéo synthétique de 1 seconde en H.265 : une fois avant le relais, et une fois en transférant le plan avant son exécution. Le service source publie réellement le résultat distinct et les tests vérifient que la source reste intacte.

La suite couvre aussi les conditions structurées : groupes ET / OU, types et données absentes, limites de taille/profondeur, instantanés d'origine/courant, disponibilité du plugin et trois branches du runner avant téléchargement. Le parcours média vérifie une condition H.265 courant ET H.264 d'origine après conversion.

Le testeur de flow est couvert sans lancer de traitement : branches et reconvergence, probes/variables, résultats fictifs de transformation, conservation de l’origine, relais avec paramètres hérités, plan non exécuté, contrats HTTP et erreurs de profil. Un fichier synthétique vérifie MediaInfo et l’intégrité de la source ; saturation et annulation vérifient la libération des ressources.

La suite vérifie également les publications inchangées et concurrentes, l’absence de nouvelles sauvegardes, la concurrence des brouillons, les modifications réelles, la version exacte retournée par l’API et la conservation des versions identiques lors d’un import d’archives.

## Frontend

`npm test` (dans `frontend/`) exécute les tests de logique pure (`node --test`) puis les tests de composants Angular (Vitest, `*.spec.ts`, via `ng test`). `npm run test:e2e` lance le parcours E2E léger Playwright (bureau et mobile, API simulée, vérifications d’accessibilité axe) ; il ne requiert ni backend ni média. En CI, les rapports et traces sont conservés en cas d’échec. Exception temporaire documentée : la règle axe `color-contrast` est désactivée dans `e2e/smoke.spec.ts` tant que les jetons de thème de la vue d’ensemble ne sont pas corrigés (12 éléments signalés).
