# Reprise IA — cluster sans quorum

## Mission et décisions imposées

Terminer D3/D4/D5 du plan utilisateur : propriété journalisée, transfert interserveurs avec accusé, checkpoints et fichiers vérifiés, continuité en partition, reprise manuelle sûre et réconciliation avant réadmission. D1/D2 (retrait Raft, maillage LWW unique) sont déjà sur dev via les PR précédentes. Branche `feat/cluster-transfers-recovery`, PR non brouillon vers `dev`. L’utilisateur demande explicitement de poursuivre après ce premier point : **la création de cette PR ne termine pas la mission**.

Décision utilisateur obligatoire : à deux serveurs, reprise uniquement après neutralisation explicite de l’ancienne installation et de tous ses workers. Ni timeout, ni gossip, ni majorité imaginaire n’autorisent une reprise. À trois serveurs, aucun basculement tant que le futur module dédié n’est pas implémenté. Ne pas réintroduire Raft ni changer le protocole du maillage.

## Avancement à l’ouverture de la PR

| Étape | État | Points d’entrée / preuves |
|---|---|---|
| Propriété, epochs, journal signé | Implémenté | TaskOwnershipRepository, TaskCoordinationProjection |
| Checkpoints chiffrés et capture différée | Implémenté | TaskCheckpointRepository, migration 0056 |
| Transfert reçu puis activé, perte d’accusé / activation | Implémenté, scénarios ciblés passés avant dernier patch | ClusterTaskCoordinator / Transport / Endpoints |
| Reprise à deux, neutralisation, réadmission signée | Implémenté, scénarios ciblés passés | ClusterTaskTransferTests |
| Isolation, gossip borné, doublons/quarantaine | Implémenté | ClusterTaskMaintenance, protections publication |
| nodes-handoff serveur et runtime 1.8 | Implémenté | nodes.json, RuntimeEligibility, NodeAgent |
| Annuler / reprendre / réessayer via propriétaire | Implémenté | ExecutionRepository et RPC signé |
| Statuts interface et propriété | Implémenté | activité, accueil, catalogue des nœuds |
| Suite runtime complète | Dernière exécution : 515 réussis, 6 ignorés | /tmp/cluster-rest-final-runtime.log ; antérieure aux derniers patches |
| Mesure 7 000 tâches, deux SQLite | Réussie sur révision intermédiaire | cluster-two-server-measurements.md ; /tmp/cluster-two-server-load2.log |
| Dernier patch de partage des téléchargements | **À compiler et tester** | ClusterTaskTransport, RuntimeArtifactStore, test ajouté |
| Frontend | Tests Node et lint passés sur révision intermédiaire | Suite Angular complète et build final à exécuter |
| CI, launcher, plugins, documentation finale | **À terminer / vérifier** | backend.yml, suites et guides |
| Machines physiques / NAS / partition réelle | **Non vérifié** | Ne pas prétendre ces garanties testées en production |

## Consignes strictes pour la reprise

1. Lire AGENTS.md, BACKEND_RULES.md, TEST_RULES.md et UI_RULES.md, puis ADR 0016 et fiches runtime/cluster/replication-storage. Respecter le writer SQLite unique ; aucun HTTP, copie média ou hash long dans sa transaction.
2. Reprendre **cette branche et cette PR**. Ne pas écraser les changements et ne pas fusionner automatiquement. Vérifier git status et le dernier commit avant toute action.
3. Ne pas déclarer fini sur la base des tests intermédiaires. Recompiler avec warnings en erreurs, exécuter les tests après les dernières modifications et mettre à jour ce tableau ainsi que la description de PR avec résultats exacts.
4. Préserver les contrôles de rôle administrateur explicites sur chaque écriture. L’endpoint interserveurs anonyme au sens session est authentifié par signature, nonce, appartenance et admission ; conserver les tests du catalogue d’accès.
5. Préserver les époques, le gel source, les tombstones d’abandon et la vérification de révision au commit. Un transfert dont l’activation a été journalisée ne peut pas être annulé. Un accusé tardif ne ressuscite pas un transfert abandonné.
6. Ne jamais modifier un média réel pour tester. Préserver source, validation, publication sans écrasement, scan des sorties et barrières des écritures destructives. Ne pas retirer un verrou sous prétexte qu’il est ancien.
7. Migration 0056 additive, tables locales explicitement classées. Elle n’est pas encore livrée à l’ouverture de cette PR ; après application hors tests ne pas la réécrire.
8. Aucun quorum sur le chemin critique. La présence relayée ne permet ni reprise ni réadmission ; les baux restent locaux au propriétaire.
9. Ne pas ajouter de fonctions facultatives avant de terminer les validations. Aucun nouveau sous-agent sans instruction explicite.
10. Publier régulièrement un avancement honnête en français. Garder une liste de limitations concrètes ; ne pas convertir les scénarios synthétiques en preuve multi-hôtes.

## Travail restant, dans l’ordre

- Compiler le dernier code puis tester transport partagé/cache, annulation pendant réception, commandes à distance, autorisations, barrière source et transferts.
- Exécuter la suite runtime complète avec outils média, puis launcher et plugins selon TEST_RULES.md. Réparer les échecs réels sans diminuer les assertions de sécurité.
- Exécuter `npm run lint`, `npm test`, `npm run build` dans frontend. Vérifier le format des fichiers modifiés.
- Relancer la mesure deux serveurs sur le commit final ; conserver la sortie JSON, compléter le rapport et rendre le scénario reproductible en CI avec artefact si approprié.
- Vérifier cohérence des documents, endpoints, migration, catalogue et compatibilité. Vérifier les différences avec dev, git diff --check, puis pousser les corrections sur cette PR et suivre la CI.
- Mettre à jour ce document et la PR : terminé / échoué / non exécuté, commandes exactes, limites. Ne demander aucune permission supplémentaire déjà donnée pour travailler/pousser cette PR.

## Environnement de cette session

Dépôt `/workspace/encoding`. SDK `/tmp/encoding-dotnet/dotnet` (.NET 10). Builds `-m:1` pour limiter la mémoire. Pour les tests média : ajouter `/tmp/encoding-mkv/root/usr/bin` à PATH et `/tmp/encoding-mkv/root/usr/lib/x86_64-linux-gnu` à LD_LIBRARY_PATH ; `ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1`. Ne pas lancer les builds Angular et .NET lourds simultanément. Les fichiers /tmp ne sont pas des artefacts durables : conserver les preuves utiles dans le dépôt ou les artefacts CI. GitHub `remi-deher/encoding`, cible `dev`. Après création, attacher la PR à la conversation.
