# Proposition d’interface IA

Maquette Angular isolée, utilisant le thème du projet et Angular Material. Toutes les données et réponses sont illustratives. Aucune API ni inférence réelle. Aucune route de production modifiée.

La page privilégie la conversation : historique à gauche, échange et saisie au centre, ressources attachées à droite. Les onglets Fournisseurs et Activité donnent accès aux connexions du cluster et aux demandes récentes. Sur mobile les zones s’empilent. Les contrôles Material gèrent clavier, focus et sélection ; le thème clair/sombre réutilise le service partagé.

Depuis `frontend/` :

```sh
npm ci
npm exec ng build -- --configuration development --browser mockups/ai/main.ts --ts-config mockups/ai/tsconfig.json --index mockups/ai/index.html --output-path dist/ai-mockup --source-map=false --output-hashing=none
node mockups/serve.mjs dist/ai-mockup/browser 4318
```

Ouvrir http://127.0.0.1:4318. Saisir un message simulé, ouvrir une conversation, joindre ou retirer des ressources, naviguer entre les onglets et basculer le thème. Le bouton « Joindre une ressource » ouvre un sélecteur regroupant flows, nœuds, bibliothèques et exécutions. Les ressources déjà jointes ne peuvent pas être ajoutées en double.

Vérification ciblée (serveur lancé) :

```sh
npx playwright install chromium
node mockups/ai/verify.cjs
```

Ce script vérifie le parcours, l’absence de débordement horizontal à 390 px, les erreurs navigateur et l’accessibilité WCAG AA du thème clair sur mobile. Il produit les captures ordinateur et mobile. Les valeurs de quotas indisponibles restent explicitement indisponibles.

L’intégration finale devra réutiliser les services IA et les confirmations existantes. Le sélecteur liste les modèles de démonstration par fournisseur (GPT 6.1, Claude Opus, Sonnet et Haiku) et reste accessible pendant la conversation. L’intégration devra alimenter la liste depuis les modèles disponibles et prendre en charge le changement de modèle côté backend. Les ressources proposées sont des exemples fictifs pour chaque catégorie.

Pour générer le fichier HTML autonome après compilation : `node mockups/ai/package.mjs`.
Le résultat est `dist/ai-mockup/browser/maquette-ia.html`.

Dans Fournisseurs, chaque modèle peut être activé ou désactivé. Les modèles désactivés disparaissent du sélecteur ; désactiver le modèle sélectionné sélectionne le premier modèle actif. Sans modèle actif, l’envoi est désactivé. Ces réglages sont simulés en mémoire et réinitialisés au rechargement. Ollama est explicitement non configuré et ne fournit aucun modèle par défaut.
