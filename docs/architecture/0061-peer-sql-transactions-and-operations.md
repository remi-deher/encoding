# 0061 — Transactions SQL répliquées et exploitation des pairs

Statut : implémenté ; complète [0060](0060-peer-replicated-cluster-state.md).

## État durable

Chaque pair conserve sa propre base SQLite et son propre journal Raft. Tous les pairs d'un groupe utilisent les mêmes migrations et la même clé `PeerCluster:SharedDataKey`. Aucune base centrale n'est nécessaire.

Les repositories utilisent `DbDataSource`. En mode pair, `PeerDataSource` intercepte les transactions ; des triggers SQLite capturent leurs lignes après modification et leurs suppressions. On réplique ces valeurs, y compris les identifiants, dates, secrets chiffrés et valeurs générées. Le SQL et ses fonctions non déterministes ne sont pas rejoués sur les autres pairs.

Avant d'ouvrir une transaction SQL sur le leader, une barrière de lecture Raft forte attend l'application des entrées engagées. Un verrou local sérialise les transactions. Les modifications partagées exigent un bail et le même terme à l'ouverture et à la confirmation. Raft engage la transaction avant la confirmation SQL et la réponse HTTP. Sur le leader, l'application de sa transaction en cours met à jour l'image immuable sans réentrer dans sa transaction SQL ; les autres pairs appliquent les lignes atomiquement. Une confirmation incertaine ferme les écritures jusqu'à reconstruction de la projection depuis l'image engagée.

Toutes les tables métier migrées sont partagées, notamment paramètres et jetons des workers, bibliothèques, accès et chemins par nœud, profils, modèles, scripts, plugins, flows, exécutions, checkpoints, baux et résultats. `users` et `user_sessions` restent locaux : chaque API authentifie ses administrateurs. Les tables de migrations et de capture sont locales. Une table partagée sans clé primaire ou une écriture dont la politique est inconnue est refusée.

Le premier pair importe l'image complète de l'unique base faisant autorité. Un nouveau pair vide reçoit cet état puis les transactions suivantes. L'image est sauvegardée dans les instantanés Raft toutes les 64 transactions ; le WAL conserve les transactions intermédiaires. Une restauration remplace les projections partagées, sans écraser les comptes locaux. Les chaînes de relance d'exécutions sont restaurées dans l'ordre de leurs références.

## API et confidentialité

Les lectures utilisent la projection locale et peuvent être en retard pendant une partition. Les mutations reçues sur un follower sont transmises au leader par TLS mutuel. Le principal déjà authentifié est transmis uniquement sur ce canal interne ; les sessions locales ne sont pas copiées. L'API publique ne peut pas injecter ce principal. Les jetons workers sont vérifiés par le leader et la protection CSRF reste appliquée. Le gossip ne réintroduit pas un nœud retiré du catalogue SQL et ne remplace pas ses paramètres durables par une ancienne observation.

Le port pair exige un certificat explicitement approuvé et les en-têtes du protocole. L'empreinte de compatibilité inclut les migrations avec leurs checksums, le schéma SQLite et l'identifiant de clé partagée. Une incompatibilité retourne HTTP 426 avant le traitement du message Raft. La jointure refuse la fusion de deux groupes indépendamment initialisés.

La clé partagée de 32 octets chiffre les secrets applicatifs avec AES-GCM. Les anciens secrets Data Protection sont rechiffrés par l'autorité lors de l'import initial. Les certificats privés, comptes administrateurs et sessions conservent leur protection locale. La clé partagée, les mots de passe d'identité et les sauvegardes doivent être conservés hors du dépôt.

## Tâches et fichiers

L'époque worker inclut le terme Raft, y compris le terme zéro, et une séquence croissante. Après une élection, les appels portant un ancien terme sont refusés même si leur bail SQL n'est pas expiré. Un effet externe de plugin déjà démarré ne peut pas être annulé par Raft : une invocation non idempotente interrompue reste en échec explicite, sans répétition automatique.

L'accès direct et le transfert WebDAV existant restent les deux moyens de traiter une bibliothèque. Les octets des médias, les artefacts et les sauvegardes de flows ne passent pas par Raft. Le stockage des artefacts et sauvegardes doit rester accessible aux serveurs qui les servent ; il relève de l'installation, comme le stockage des médias. Si aucune source directe configurée n'est disponible, les tâches concernées échouent dès la demande d'attribution ou la prochaine passe de reprise (une seconde), et libèrent leurs réservations. Un échec de transfert provoque aussi l'échec de la tâche.

## Exploitation et limites du protocole initial

Les commandes de sauvegarde à froid et restauration vérifient le verrou de l'identité, les empreintes de fichiers et les compteurs SQL. Un ensemble coordonné conserve les trois identités, leurs bases, clés locales, membership et journaux. Les répertoires et bases cibles doivent être vides. Ne jamais démarrer deux copies d'une même identité.

La rotation prépare un nouveau certificat, engage le chevauchement de confiance dans Raft, active le certificat sur le pair arrêté, vérifie son retour, puis retire l'ancien certificat par consensus. La présence signée accepte les deux certificats durant le chevauchement.

La première politique de mise à jour est coordonnée : même version sur les pairs ; aucun engagement de compatibilité N/N-1. Un changement de schéma ou de protocole exige une migration hors ligne depuis une sauvegarde faisant autorité vers un nouveau groupe, avec validation propre à la version. Ne pas démarrer un nouveau schéma sur d'anciens instantanés ni effacer un WAL pour contourner le contrôle de compatibilité. Les transactions et l'image d'import initiale sont limitées à 64 Mio par commande : un import trop volumineux est refusé, pas tronqué.

## Validation

`PeerClusterIntegrationTests` lance trois API avec bases et volumes indépendants, isole un leader par des proxys CONNECT de test, vérifie l'élection majoritaire, le refus des écritures isolées et des anciens baux, la convergence au retour, la rotation, la restauration d'un pair puis des trois pairs et le rejeu d'une transaction postérieure au dernier instantané. Les tests de projection, migration et sauvegarde utilisent SQLite. Les incompatibilités de protocole et d'empreinte sont testées avec un certificat valide.
