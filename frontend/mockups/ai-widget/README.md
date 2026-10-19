# Assistant flottant compact

Proposition visuelle isolée : bulle de 56 px en bas à droite et fenêtre non modale de 380 × 520 px. L’application reste utilisable derrière. Sur mobile, la fenêtre s’adapte à la largeur et à la hauteur disponibles.

La fenêtre contient uniquement l’en-tête avec réduction, le modèle, les messages et la saisie. Le raisonnement est accessible dans le menu de réglages ; le trombone joint une ressource. Entrée envoie, Maj+Entrée ajoute une ligne. Échap ou Réduire ferment la fenêtre et rendent le focus au lanceur. Réouvrir conserve les messages et la saisie, en mémoire pendant la démonstration.

La maquette utilise Angular Material, CDK Overlay et les jetons de thème partagés. Aucun composant de production n’est modifié. Le catalogue de démonstration est une fixture typée `AiAssistantModelOption` ; l’intégration utilisera les options du backend déjà implémentées. Les réponses sont explicitement simulées. Aucune requête d’inférence ou API n’est envoyée.

Depuis `frontend/` :

```sh
npm exec ng build -- --configuration development --browser mockups/ai-widget/main.ts --ts-config mockups/ai-widget/tsconfig.json --index mockups/ai-widget/index.html --output-path dist/ai-widget --source-map=false --output-hashing=none
node mockups/serve.mjs dist/ai-widget/browser 4319
```

Ouvrir http://127.0.0.1:4319, puis cliquer sur la bulle. Pour produire un fichier autonome : `node mockups/ai-widget/package.mjs`. Résultat : `dist/ai-widget/browser/maquette-widget-ia.html`.

Vérification avec le serveur lancé : `node mockups/ai-widget/verify.cjs`. Le parcours couvre choix du modèle et du raisonnement, pièce jointe, envoi, réduction/réouverture, conservation des messages, focus, Échap et WCAG AA en clair/sombre aux largeurs 1440 et 390 px. Les captures sont générées dans ce dossier.
