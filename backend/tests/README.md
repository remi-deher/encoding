# Vérifications du runtime

La suite utilise les vrais dépôts, PostgreSQL, les endpoints HTTP du runtime et le runner worker. Elle ne dépend d'aucun service personnel. Les données sont créées dans un schéma PostgreSQL unique `handoff_<uuid>` puis supprimées, et les fichiers synthétiques restent dans un dossier temporaire propre à la suite.

Avec .NET 10 :

```powershell
$env:ENCODING_TEST_DATABASE = 'Host=localhost;Database=encoding_test;Username=encoding;Password=encoding'
dotnet run --project backend/tests/Encoding.Runtime.Tests --configuration Release
```

Sans cette variable, les contrats, les règles de sélection, la compatibilité et le testeur de flow (dont l’import synthétique MediaInfo) sont vérifiés. FFmpeg, ffprobe et MediaInfo sont nécessaires. `backend/tests/Dockerfile` fournit le SDK et ces outils. La CI fournit PostgreSQL et exécute la suite complète.

Depuis la racine du dépôt, avec Docker et une base de test éphémère :

```powershell
docker build -t encoding-runtime-tests -f backend/tests/Dockerfile backend/tests
docker run --rm -d --name encoding-runtime-test-db -e POSTGRES_USER=encoding -e POSTGRES_PASSWORD=encoding_test -e POSTGRES_DB=encoding_test postgres:17
docker exec encoding-runtime-test-db pg_isready -U encoding -d encoding_test
# Attendre que pg_isready annonce une base disponible.
docker run --rm --network container:encoding-runtime-test-db -e 'ENCODING_TEST_DATABASE=Host=127.0.0.1;Database=encoding_test;Username=encoding;Password=encoding_test' -v "${PWD}:/source" -w /source encoding-runtime-tests dotnet run --project backend/tests/Encoding.Runtime.Tests --configuration Release
docker stop encoding-runtime-test-db
```

Les scénarios média encodent une vidéo synthétique de 1 seconde en H.265 : une fois avant le relais, et une fois en transférant le plan avant son exécution. Le service source publie réellement le résultat distinct et les tests vérifient que la source reste intacte.

La suite couvre aussi les conditions structurées : groupes ET / OU, types et données absentes, limites de taille/profondeur, instantanés d'origine/courant, disponibilité du plugin et trois branches du runner avant téléchargement. Le parcours média vérifie une condition H.265 courant ET H.264 d'origine après conversion.

Le testeur de flow est couvert sans lancer de traitement : branches et reconvergence, probes/variables, résultats fictifs de transformation, conservation de l’origine, relais avec paramètres hérités, plan non exécuté, contrats HTTP et erreurs de profil. Un fichier synthétique vérifie MediaInfo et l’intégrité de la source ; saturation et annulation vérifient la libération des ressources.

La suite vérifie également les publications inchangées et concurrentes, l’absence de nouvelles sauvegardes, la concurrence des brouillons, les modifications réelles, la version exacte retournée par l’API et la conservation des versions identiques lors d’un import d’archives.

Les tests de remplacement de source (fichiers, politique, période de grâce, restauration) et de bascule multi-nœud (perte d’un worker, d’un nœud d’accès, d’un serveur) sont décrits dans l’architecture 0030. Le remplacement réel d’une source H.265 puis sa restauration sont aussi vérifiés avec FFmpeg.
