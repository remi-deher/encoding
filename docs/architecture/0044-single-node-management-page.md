# 0044 — Gestion du nœud et du cluster depuis la page Nœuds

Statut : implémentée (demande de Rémi : « mettre toute la gestion dans Nœuds », ne plus mélanger worker et nœud).

## Situation avant

La console worker (`/worker`) était une page isolée : publique, sans menu, parlant directement à l'agent sur un autre port (5081 puis 8081). Elle datait du modèle « une installation = un worker autonome » et obligeait, avec l'image unique, à jongler entre la page Nœuds (serveur) et la console (agent), avec deux ports, des réglages CORS et deux vocabulaires.

## Décision

- **Vocabulaire** : « nœud » partout ; « worker » n'est qu'un rôle d'un nœud, comme « serveur ». « Console worker » devient « Ce nœud », « Repérer le worker » devient « Repérer le nœud ».
- **Une seule entrée** : la page Nœuds porte « Ce nœud et son cluster » (mode, adresse d'un cluster, clé de jumelage, rejoindre, quitter) à côté de la liste des nœuds et de « Ajouter un nœud ». Le contenu est un composant partagé (`app-local-node-panel`).
- **Relais authentifié** : le serveur expose `/api/v1/local-node/{status,clusters,enroll,pairing-key,leave}` et les transmet à l'agent du même conteneur (`LocalAgent:BaseUrl`, par défaut `http://127.0.0.1:{WorkerUi:Port}/`). Les écritures suivent la règle commune (session administrateur, en-tête anti-CSRF). Le rôle lecture voit l'état mais ni la clé de jumelage ni le code de vérification. Sans fonction worker active, le relais répond 503 avec un message qui renvoie vers « Mode du nœud ».
- Le navigateur n'appelle plus l'agent : aucun port 8081 à publier, plus de CORS (`WorkerUi__Origins`), fonctionne depuis n'importe quel poste.
- **Canal interne** : l'agent écoute uniquement en boucle locale. Toutes ses routes `/api/v1/worker/**`, lectures comprises, exigent le secret de l'image unique déjà fourni par le lanceur (`Node:LocalEnrollmentSecretFile`, architecture 0037). Le relais lit le fichier à chaque appel, transmet le secret uniquement à une adresse de boucle locale et ne suit pas les redirections. Le contrôle `Origin` et CORS de l'agent sont supprimés ; `WorkerUi__Origins` n'est plus utilisé. Sa route de santé reste accessible sans secret.
- **Nœud sans serveur** : la gestion par navigateur nécessite d'activer la fonction serveur locale. L'ancien accès direct du navigateur à l'agent n'est plus un canal de gestion ; un client interne doit disposer du secret local. Hors lanceur, le serveur et l'agent doivent recevoir explicitement le même fichier secret ; aucun accès non authentifié de repli n'existe.

## Impacts

Nouvelle route d'API (fermée par défaut, aucune ouverture anonyme), composant partagé, libellés de la page Nœuds. Les parcours de jumelage et d'enrôlement (0039, 0040) sont inchangés.

## Vérifications

Scénario d'authentification : lecteur refusé en écriture, lecture anonyme refusée, refus clair sans fonction worker, masquage de la clé et du code pour le rôle lecture.

Régression du rattachement manuel : appel relayé sans `Origin` accepté avec le secret, refus sans secret ou avec un secret incorrect même si `Origin` est fourni, rotation du secret prise en compte et absence de fuite vers une adresse distante. Cette protection commune couvre ajout/oubli de cluster, enrôlement, jumelage, départ et mode du nœud.
