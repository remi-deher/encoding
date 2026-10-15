# 0045 — Nœud autonome et cluster explicite

Statut : implémentée (demande de Rémi) ; remplace le point 1 de [0038](0038-cluster-and-node-model.md) (« cluster implicite à un nœud »). Les groupes de clusters sont reportés.

## Vocabulaire

- **Nœud autonome** : une installation seule, qui n'appartient à aucun cluster.
- **Cluster** : unité de gestion qui regroupe des nœuds (état partagé, baux, bibliothèques, administration) ; frontière d'autorité et de cohérence.
- **Groupe de nœuds** : classement de nœuds à l'intérieur d'un cluster ([0034](0034-node-groups-and-labels.md)), sans effet sur la gestion.

## Décision

- L'identité interne du cluster (identifiant, base) subsiste, mais l'interface et l'API n'exposent un cluster que lorsqu'il existe : `standalone` vaut vrai tant que `cluster_identity.established` est faux **et** qu'il y a au plus un nœud actif.
- Un cluster existe dès qu'un second nœud est rattaché (rien d'autre à faire) ou quand un administrateur en crée un en le nommant (`POST /api/v1/cluster`, migration 0034). Le nom se change ensuite avec `PUT /api/v1/cluster`. Créer un cluster déjà créé, ou renommer un nœud autonome, répond 409.
- `GET /api/v1/cluster` (public) ajoute `standalone`. Le relais « ce nœud » (0044) l'ajoute à l'état local quand le nœud n'est rattaché qu'à son cluster interne.
- Le panneau « Ce nœud » affiche « Nœud autonome » ou « Dans un cluster » ; un nœud autonome propose « Créer un cluster » et « Rejoindre un cluster » et n'a pas de « Quitter ».
- Aucune migration de données : les instances actuelles à un nœud deviennent autonomes ; celles à plusieurs nœuds restent des clusters.

## Impacts et limites

Contrat de `GET /api/v1/cluster` (champ ajouté), migrations PostgreSQL et SQLite 0034, panneau « Ce nœud ». La découverte réseau annonce toujours le nœud, même autonome. Un cluster qui retombe à un seul nœud non créé explicitement redevient autonome.

## Vérifications

Scénario d'authentification : autonome par défaut, création refusée au rôle lecture, nom obligatoire, création puis 409 au second appel, renommage, 409 sur un nœud autonome ; suite complète sur PostgreSQL et SQLite.
