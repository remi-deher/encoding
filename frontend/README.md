# Frontend

Application Angular du projet de traitement de bibliothèques multimédias.

## Développement local

```powershell
npm ci
npm start
```

L’application est ensuite disponible sur `http://localhost:4200`.

## Compilation

```powershell
npm run build
```

Le frontend utilise Angular Material pour ses composants partagés. L’accueil lit les nœuds et bibliothèques via les API du cluster et s’actualise toutes les 10 secondes. Les exécutions et leur chronologie proviennent du registre PostgreSQL, actualisé toutes les cinq secondes. Les économies ne sont pas encore mesurées. Les workers n’exécutent pas encore de flows ; le registre est vide tant qu’aucune attente n’y est enregistrée. Le catalogue et l’éditeur de flows restent en mémoire ; ils ne lancent pas encore les scripts ou les outils média.

## Vérifications

```powershell
npm run lint
npm run format:flows
npm test
npm run build
```

Le lint contrôle les types TypeScript et les déclarations inutilisées. La vérification de format couvre les composants et modèles de l’éditeur. Les tests rapides utilisent le runner Node et protègent les règles du graphe (entrée unique, reconvergence, cycles, sorties et champs obligatoires). Le workflow GitHub Actions Frontend exécute ces mêmes commandes.

## Éditeur de flows

Chaque flow comporte une entrée obligatoire, générique ou spécialisée selon son type. Les actions Script, Préparer/Exécuter le traitement, Reporter et Terminer sont disponibles dans le catalogue. Une sortie peut ajouter un nouveau bloc ou rejoindre un bloc existant ; plusieurs chemins alternatifs peuvent converger.

Configurer une étape depuis son bouton. Appliquer au flow modifie le brouillon en mémoire. L’éditeur présente une séquence verticale et des branches repliables, avec les sorties déclarées par les plugins. Ajouter une étape insère le bloc sans supprimer la suite ; Ajouter une suite aux chemins non terminés crée une étape commune. Les étapes simples se réordonnent par glissement CDK ou avec Monter/Descendre. Les raccordements partagés complexes restent des renvois explicites et les blocs détachés sont conservés séparément. Vérifier le flow contrôle les connexions et les champs obligatoires sans exécuter les scripts. Les positions historiques restent des métadonnées de compatibilité et ne déterminent plus l’affichage.

## Blocs de référence

Le bloc structuré est le gabarit partagé. Déclarer inline: true sur un champ du schéma pour l’afficher dans le bloc ; les champs sont rendus dans leur ordre de déclaration. Les nombres peuvent déclarer suggestions pour l’édition par liste. Le panneau détaillé permet de saisir les autres valeurs valides. Les contrôles directs et le panneau partagent la validation. Voir docs/architecture/0011-flow-block-template.md pour le contrat et la frontière avec le futur backend des plugins.
