# Cluster sans quorum : revue et découpage

Statut : cadrage de la proposition de Rémi, reçue le 25 octobre 2026. Le texte reçu s'interrompt dans D4, au début de la présence transitive ; D5 n'a pas été reçu. Ce document ne définit pas les règles de reprise manquantes et ne prétend pas les implémenter.

## Avis

Retirer le mode Raft optionnel et conserver le maillage comme modèle unique est cohérent avec deux serveurs autonomes. LWW convient aux projections métier partagées ; il ne prouve pas la possession exclusive d'une tâche. Le plan d'attribution doit séparer identité de tâche, propriété d'exécution et autorisation de publier.

Le principe sûr à deux serveurs est la continuité du travail déjà possédé, avec transfert coopératif. Un timeout, même confirmé par un tiers, ne prouve pas qu'un propriétaire est arrêté. Une reprise sans contact exige de neutraliser son autorité précédente ou d'accepter explicitement des doublons et de protéger les écritures finales. À trois serveurs, une majorité portant sur la propriété ne suffit que si les anciens propriétaires et le lieu de publication appliquent cette décision : un serveur isolé ne doit pas garder un droit de remplacement illimité après une reprise par la majorité.

## Points à préciser avant D3–D5

1. **Transfert et accusé perdu.** Si B journalise un accusé et commence, mais que A ne le reçoit pas et continue, les deux exécutent. Distinguer une réception durable de préparation d'une activation. Une proposition de transfert ne donne aucun droit d'exécuter. Une cession engagée impose que la source ne puisse plus reprendre à l'ancienne époque ; après doute, interroger/rejouer idempotemment la même opération, sans interpréter le timeout comme un refus certain. Garantir simultanément aucune interruption, une terminaison bornée du transfert et une propriété globalement observable pendant toute partition n'est pas possible par simple accusé. Définir précisément ce que signifie « jamais en limbo ».
2. **Accusé par un tiers.** Un tiers peut attester la conservation d'une intention, mais ne peut pas autoriser seul une cible ni révoquer le propriétaire. Définir le destinataire qui exécute, l'identité du signataire autorisé et les conditions d'activation.
3. **Propriété initiale et doublons.** Une propriété unique par tâche n'empêche pas deux tâches différentes sur le même média. La clé média doit être portable et représenter le même fichier entre accès et alias. La réservation actuelle est locale ; sa réplication et la résolution des collisions constituent une évolution explicite. Une règle de gagnant par date UTC suppose des horloges fiables et ne tranche qu'après rencontre des événements.
4. **Publication pendant une partition.** La quarantaine après réconciliation ne protège pas une source déjà remplacée. La politique de remplacement est facultative, mais le bloc `mkv-edit-in-place` peut aussi écrire dans la source. La période de grâce est une possibilité de restauration, pas un verrou. Prévoir un protocole de fencing au point d'écriture ou limiter les opérations conflictuelles en partition. Deux résultats distincts validés peuvent être conservés sans garantir une seule conversion.
5. **Retour d'un serveur.** Définir la barrière locale de réconciliation avant réactivation, les checkpoints et artefacts nécessaires, et le comportement si les pairs restent injoignables. Distinguer continuer une propriété inchangée et reprendre une tâche dont l'autorité a pu être cédée. Aucune règle ne doit inventer la certitude à partir d'une présence observée.
6. **Gossip.** Conserver observateur, sujet, identité de démarrage, instant d'observation et fraîcheur ; une observation relayée n'est ni un heartbeat direct ni un mandat de reprise. Éviter qu'une ancienne observation se rafraîchisse à chaque relais. Garder la disponibilité locale indépendante du gossip.
7. **Plan borné.** Un événement contenant toute une découverte peut dépasser la limite des échanges (16 Mio). Définir un plafond par plan ou des fragments avec manifeste et activation complète. Réévaluer l'éligibilité au claim ; rendre visibles les affectations devenues impossibles.
8. **Époques.** L'époque locale du bail worker et la génération de propriété inter-serveurs ont des responsabilités distinctes. Les messages et checkpoints doivent porter l'identité de la tâche, le propriétaire, la génération et l'identifiant idempotent de transfert. Un compteur concurrent fusionné en LWW ne constitue pas un fencing global.

## Audit de D1

Le retrait dépasse le dossier `Infrastructure/PeerCluster/` :

- `Program.cs` compose le coordinateur, les sources de données, les commandes CLI Raft et les routes du mode pair.
- `Infrastructure/Data/DatabaseProvider.cs` désactive actuellement l'écrivain local quand `PeerCluster:Enabled` est vrai.
- `PeerDataSource`, `PeerDatabaseSchema`, `PeerOperationsCommand` et les accès SQL bruts servent le chemin Raft.
- Les dépôts runtime, reprise, attente, bibliothèques, nœuds, flows et plugins utilisent les gardes d'attribution et/ou les époques du mode pair.
- `Logging/SystemLogRepository.cs` dépend de `PeerLocalDatabase` pour ses journaux locaux. Retirer l'enveloppe en conservant la sémantique locale des logs.
- Les métriques, la rétention, les protections de secrets, l'identité, l'authentification et les endpoints ont des références à examiner. Conserver les identités, signatures, protections et routes du **maillage** : le mot « peer » ne signifie pas automatiquement Raft.
- Supprimer les tests exclusivement Raft, garder les tests de sécurité des pairs du maillage, de ressources entre pairs et de capture incrémentale ; adapter leurs références éventuelles aux utilitaires retirés.
- Le workflow `monitoring.yml` ne teste que les alertes Raft. Mettre à jour les scopes et leurs tests, les exclusions des workflows backend/image, et les références des documents d'exploitation.
- La dépendance `DotNext.AspNetCore.Cluster` peut être retirée après suppression de ses usages. Les migrations historiques déjà appliquées ne doivent pas être modifiées.

### Mise à jour d'une installation existante

Avant la suppression, définir le traitement explicite de `PeerCluster__Enabled=true`. Ne pas ignorer silencieusement cette option et démarrer plusieurs anciennes projections comme des propriétaires autonomes. Fournir un diagnostic de configuration et un chemin de sortie documenté du mode Raft. Vérifier les sauvegardes et l'état local sans convertir ni supprimer automatiquement des données de production.

## Livraison proposée

1. **D1–D2 : retrait du mode Raft.** ADR de retrait, source SQLite locale et écrivain commun uniques, nettoyage DI/CLI/gardes/métriques/tests/CI/documentation. Aucun changement du format du journal ou du protocole du maillage, aucune reprise inter-serveurs ajoutée. Tests : démarrage autonome, indépendance à la panne du pair, migrations, writer, sécurité et réconciliation existante.
2. **D3 : propriété et transfert coopératif.** Après cadrage du protocole, contrat versionné, stockage de propriété et des intentions, accusés durables et idempotents, transitions et fencing, checkpoint et artefacts vérifiés, claims locaux. Tester les pertes de messages à chaque transition, doublons, redémarrages et réordonnancement. Déclarer chaque nouvelle table locale ou réconciliée et prévoir les versions mixtes.
3. **D4 : comportement en partition et observations.** Continuité locale, limites explicites pour les écritures média conflictuelles, présence avec provenance et fraîcheur, diagnostics. Tester partitions asymétriques, créations concurrentes et retour du réseau.
4. **D5 : reprise.** En attente du texte complet. Ne pas assimiler la perte du worker à la perte du serveur propriétaire. À deux serveurs, ne pas déclarer une reprise sûre sur le seul critère « pair hors ligne ». Tester le retour de l'ancien propriétaire après achèvement ailleurs avant toute activation automatique.

Le numéro du prochain ADR doit être réservé après consultation du registre et des PR ouvertes au moment d'implémenter. Cette revue ne modifie aucun contrat d'exécution ni invariant en production.
