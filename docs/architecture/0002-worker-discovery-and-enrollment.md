# 0002 — Découverte et enrôlement des workers

- Statut : accepté
- Date : 13 octobre 2026

## Décision

L’agent worker expose une console locale à deux sections : **Tâches** et **Nœud**. Il recherche les clusters via DNS-SD/mDNS sur `_encoding._tcp`. L’utilisateur confirme le rattachement depuis le poste worker. Une URL bootstrap configurée peut compléter la découverte quand le multicast ne traverse pas le réseau ou un bridge Docker.

Chaque installation génère un UUID et un secret aléatoire persistants dans son répertoire de données. L’agent transmet le secret lors de l’enrôlement ; l’API ne garde que son empreinte SHA-256 et vérifie cette empreinte pour chaque heartbeat. Le jeton n’est ni renvoyé à l’interface ni écrit dans les journaux. Répéter l’enrôlement avec la même identité permet de reprendre un appel interrompu.

Deux modes d’acceptation sont disponibles. La confirmation locale rejoint directement un cluster choisi. En mode clé, le worker crée une clé hexadécimale aléatoire à usage unique valable 10 minutes ; l’API ne garde que son empreinte et le serveur enrôle ce worker après saisie de la clé. Le worker détecte l’approbation lors d’un heartbeat réussi. L’acceptation automatique côté worker est désactivée par défaut ; la variable `Worker__AutoAcceptEnrollment` l’active et n’accepte automatiquement que lorsqu’un seul cluster est détecté.

## Sécurité et limites

mDNS annonce une identité et une adresse, sans authentifier le serveur. La confirmation dans le navigateur garantit le choix explicite du cluster par l’utilisateur, mais ne constitue pas une preuve vérifiable par le serveur. L’approbation d’un jumelage exige un administrateur et l’enrôlement direct d’une nouvelle identité exige une fenêtre ouverte par un administrateur ; voir [0031](0031-authentication-roles-and-pairing.md). Les déploiements hors du développement local doivent employer HTTPS et limiter l’accès réseau.

Le worker n’accepte les requêtes de modification de sa console locale que depuis les origines configurées. Le navigateur appelle l’agent sur `localhost`, afin que l’interface chargée depuis le serveur agisse sur la machine où se trouve l’utilisateur. L’API locale de l’agent doit rester liée à l’hôte local lors de son exposition par Docker.

Le multicast mDNS n’est pas garanti entre sous-réseaux, hôtes Docker ou VLAN. La configuration bootstrap et l’URL annoncée `Cluster:AdvertisedApiBaseUrl` couvrent les topologies où une adresse directe joignable est requise. Le comportement exact dépend des réseaux et ports exposés par l’installation. Les routes de gestion exigent une session utilisateur depuis [0031](0031-authentication-roles-and-pairing.md).

## Conséquences

- Aucun secret commun ne doit être copié entre workers.
- La perte du volume local rend le worker inconnu ; il devra être enrôlé avec une nouvelle identité.
- Les migrations `0003_node_enrollment.sql` et `0004_node_pairing.sql` ajoutent l’identité persistante du cluster, l’empreinte de secret et les demandes temporaires de jumelage.
- La file visible dans la console reste vide/inactive tant que le moteur et le répartiteur ne sont pas disponibles.
