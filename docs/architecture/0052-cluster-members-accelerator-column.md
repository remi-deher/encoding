# 0052 — Colonnes Matériel et Accélérateur graphique des membres du cluster

Statut : accepté (maquette validée par le propriétaire du projet).

## Décision

Dans l’onglet **Cluster**, la colonne « Capacités observées » (liste d’outils et de versions) est remplacée par deux colonnes de la liste des membres :

- **Matériel** : système et architecture, modèle de processeur et cœurs, mémoire installée, puis processeur graphique (inventaire déclaré par l’agent, voir 0050).
- **Accélérateur graphique** : pastilles calculées par `nodeAcceleratorBadges` (`shared/models/node-hardware.ts`).

Les outils et versions restent consultables dans la fiche du nœud (onglet Capacités) et dans « Ce nœud ».

## Règles d’affichage

- Première pastille : le premier encodeur **vérifié par mire** (`encoderChecks` à `usable`, ou `accelerationTypes`), dans l’ordre NVENC, Quick Sync, VAAPI, soit la priorité par défaut des profils (0027).
- Pastilles suivantes : autres accélérateurs vérifiés, puis ceux seulement **détectés** (« … détecté »), jamais utilisés en mode automatique.
- Aucun accélérateur matériel vérifié : « CPU ».
- Sans fonction worker : « Non applicable ». Sans capacité déclarée : « Non déclaré ». Aucun CPU n’est supposé sans information.
- Aucune charge CPU/GPU n’est affichée : l’API n’en fournit pas.

## Effets

Données déjà exposées par `GET /api/nodes` (`inventory`, `capabilities[].encoderChecks`) : aucune migration ni changement de contrat. Les pastilles passent à la ligne dans leur colonne sans déborder sur « Traitements ».
