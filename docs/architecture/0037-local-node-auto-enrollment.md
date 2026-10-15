# 0037 — Nœud local enrôlé d'office et nom choisi à l'installation

Statut : implémentée (demande de Rémi : dans l'image unique, le nœud qui accompagne le serveur est toujours enrôlé, avec un nom convivial choisi à l'installation).

## Situation avant

Avec `Worker__AutoAcceptEnrollment=false` (défaut), le worker local d'une image unique ne publiait aucune demande : il restait inconnu du serveur et la page Nœuds affichait 0 nœud, sans rien à approuver. Il fallait passer par la console du worker (port 8081) pour jumeler.

## Décision

- **Enrôlement d'office** : le lanceur crée à chaque démarrage `local-enrollment.secret` (aléatoire, droits propriétaire seul) dans le dossier de données du nœud et passe seulement son chemin (`Node__LocalEnrollmentSecretFile`) au serveur et à l'agent. Un fichier plutôt qu'une variable d'environnement : les scripts et plugins sous bac à sable n'héritent pas du secret.
- L'agent, quand la fonction serveur locale est active et que son identité n'est pas déjà rattachée ailleurs, appelle `POST /api/v1/nodes/enroll` sur le serveur local avec l'en-tête `X-Local-Enrollment-Secret`. Le serveur compare à durée constante avec le fichier ; en cas de succès il traite la demande comme si la fenêtre d'enrôlement était ouverte. Sans en-tête valide, rien ne change (fenêtre ou clé de jumelage). Aucune nouvelle route anonyme.
- Le secret n'est présenté qu'à un cluster en boucle locale dont l'adresse est celle configurée pour le conteneur ; jamais à un cluster distant.
- Un nœud qui a choisi un autre cluster (jumelage en cours ou enrôlé) n'est pas ramené au serveur local.
- **Nom convivial** : l'assistant de création du premier administrateur propose « Nom de ce nœud » (`nodeDisplayName`, 128 caractères au plus). Le nom est appliqué au nœud local à son enrôlement quel que soit l'ordre des deux événements ; il ne remplace jamais un nom déjà choisi. Modifiable ensuite depuis « Nom et étiquettes ».

## Impacts et limites

Contrat `SetupRequest` (champ facultatif ajouté), `Encoding.Core` (`LocalEnrollment`), lanceur, agent, assistant d'installation. Aucune migration. Le nom en attente est gardé en mémoire du serveur jusqu'à l'enrôlement local ; il est perdu si le serveur redémarre entre la création de l'administrateur et cet enrôlement (quelques secondes). Rejoindre un cluster existant à l'installation n'est pas traité ici.

## Vérifications

Scénario d'authentification (secret faux ou vide refusé fenêtre fermée, secret valide enrôlé, nom appliqué, autres nœuds toujours refusés), tests du lanceur (création, droits, renouvellement, comparaison, boucle locale) et essai de bout en bout hors Docker avec les trois programmes publiés : nœud local en ligne, serveur+worker, nom choisi à l'installation.
