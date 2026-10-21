# Reprise de la refonte des blocs de flow

## État de livraison

Branche : `codex/flow-editor-compact-branches`, PR existante vers `dev` :
https://github.com/remi-deher/encoding/pull/189.

Les quatre lots principaux sont committés :

| Commit | Contenu |
| --- | --- |
| `56cb9f6` | Importance explicite des champs, sections, visibilité et résumé du formulaire commun |
| `2343872` | Sélecteurs déclaratifs et contexte de l'éditeur |
| `a3c0e85` | Sept opérations ciblées de sous-titres et de pistes, planificateurs communs |
| `e20b5cf` | Règles audio éditables, sélection audio, redimensionnement, normalisation, styles ASS |

Le commit contenant ce document est une sauvegarde de l'étape 5 **en cours**,
pas une déclaration de validation finale. Ne pas fusionner sans terminer les
vérifications ci-dessous. La branche contient aussi le commit antérieur `07988d3`
de suppression confirmée des flows.

## Travail de l'étape 5 déjà présent

- Conservation des valeurs historiques et inconnues à l'édition ; `legacyOnly`
  masque la politique de publication d'Attendre pour les nouvelles configurations.
- Versions minimales de plugins par composant ou option et contrôle d'éligibilité,
  y compris via les anciens alias Base.
- Inventaire facultatif et borné des jeux de polices dans `NodeInventory` et
  suggestions issues des workers observés.
- Références `artifact:<identifiant-encodé>/<indice>` aux fichiers additionnels d'un
  producteur précédent, variables durables, validation du graphe et capacité worker
  `artifact-references`. Ce préfixe contient un caractère interdit dans les anciens
  chemins de bibliothèque et ne réinterprète donc pas un chemin auparavant valide.
- Règles repliables avec résumé et critères avancés ; catalogue enrichi pour les
  agents IA ; tests de compatibilité et de références.

## Vérifications déjà obtenues et limites

- Étape 4 : lint/build Angular et 33 tests backend ciblés, dont FFmpeg sur un média
  synthétique, réussis.
- Ensuite : 93 tests de modèles + 136 tests Angular réussis et 52 E2E bureau/mobile
  réussis. Ces résultats précèdent les derniers ajouts polices/références et le
  repli des règles : les refaire sur le dernier commit.
- Suite backend complète précédente : 257 réussites et un échec de comparaison
  CRLF/LF dans `AiToolTests.RuntimeSubtitleTranslation_UsesBoundedBatches_AndKeepsCueTiming`.
  Le test a été normalisé, puis les tests ciblés concernés ont réussi.
- Les derniers tests ciblés de références et de compatibilité ont réussi avant les
  tout derniers ajustements. La dernière suite complète a été interrompue à la
  demande de l'utilisateur, après compilation, sans résultat final.
- Avertissements de build connus : léger dépassement du budget initial et budget
  du SCSS du tableau de données. Ne pas relever les seuils pour les masquer.

## Plan pour l'agent repreneur

1. Lire `AGENTS.md`, `UI_RULES.md`, `BACKEND_RULES.md`, `TEST_RULES.md`, puis ADR 0079 (flow-component-usability) et 0078 (blocs compacts).
   Examiner le dernier commit et son diff avant toute modification. Ne pas refaire
   les quatre lots déjà livrés et ne pas modifier silencieusement les flows publiés.

2. Stabiliser le formulaire commun et les règles repliables. Vérifier l'ajout,
   l'ordre, la suppression, les résumés, l'ouverture des critères avancés et les
   erreurs. Contrôler les champs conditionnels, les secrets, les valeurs historiques
   et le focus clavier. Refaire les captures bureau/mobile et les contrôles
   d'accessibilité ; ajuster les tests si les règles doivent être ouvertes avant
   interaction. Vérifier que le bouton des critères avancés disparaît quand il n'y
   en a aucun. Centraliser la visibilité des champs entre formulaire et paramètres
   inline si nécessaire : un numéro de piste ne doit pas être trompeur lorsqu'une
   sélection par critères est active.

3. Auditer et tester les versions minimales. Ancien flow + ancien worker compatible
   doit conserver son comportement ; option nouvelle + worker trop ancien doit
   être refusé. Couvrir les alias Base, les plans repris après checkpoint et les
   plugins externes, sans assouplir leur compatibilité par accident. Examiner les
   consommateurs API, MCP, simulation, descriptions IA et validation des manifestes.

4. Terminer la validation des ressources. Pour les polices : inventaire absent,
   vide, inaccessible, worker hors ligne, noms inconnus et sélecteur. Un jeu observé
   sur un worker ne prouve pas sa présence sur tous les workers éligibles : examiner
   ce point avant de promettre une attribution compatible. Pour les fichiers
   produits : ajouter un parcours runtime réel extraction → consommation, puis
   reprise/relais, branche sans production, référence future, indice absent,
   empreinte invalide et rejet des traversées de chemins. Vérifier que les fichiers
   produits ne sont jamais demandés comme fichiers source à la bibliothèque et que
   les fichiers additionnels restent accessibles par le stockage durable existant.

5. Examiner les combinaisons média importantes : conversion audio + normalisation
   dans le même plan (codec final et filtres), règles déjà conformes et fréquence
   d'échantillonnage, dimensions après d'autres filtres vidéo, styles ASS complexes.
   Réutiliser les politiques et planificateurs communs ; conserver la source et
   utiliser uniquement des médias synthétiques pour ces vérifications.

6. Exécuter les vérifications finales, corriger les échecs sans les masquer, puis
   documenter les limites restantes. Mettre à jour ADR 0079 et le guide utilisateur
   avec les sélecteurs et les versions minimales réellement livrés. Faire un commit
   distinct de stabilisation, pousser et actualiser la description de la PR vers
   `dev`. Ne pas fusionner ni déployer sans demande explicite.

## Commandes locales

Dans `frontend/` :

```powershell
npm run lint
npm run build
npm test
npm run test:e2e -- e2e/flow-editor.spec.ts --workers=2
```

À la racine, utiliser le SDK .NET 10 du profil utilisateur (la commande système
peut pointer vers .NET 8) :

```powershell
C:/Users/remi2/.dotnet/dotnet test backend/tests/Encoding.Runtime.Tests
git diff --check
```

Le formatage des fichiers existants sous Windows peut nécessiter
`--end-of-line auto`. Les mutations Git de ce worktree peuvent demander une
élévation car ses métadonnées sont dans le dépôt principal hors worktree.
Ne jamais afficher de jeton. Si `gh` utilise un jeton d'environnement expiré,
retirer uniquement `Env:GITHUB_TOKEN` du processus pour utiliser l'authentification
Windows déjà configurée.
