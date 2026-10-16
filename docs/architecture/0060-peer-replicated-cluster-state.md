# 0060 — État du cluster répliqué entre pairs

Statut : direction acceptée par Rémi le 2026-10-16 ; remplace pour la cible les décisions de stockage partagé de [0001](0001-shared-state-and-node-presence.md). Le mode en production reste celui de [0043](0043-sqlite-and-data-access-abstraction.md) jusqu’à livraison complète de cette architecture.

## Objectif

Chaque serveur membre conserve localement l’état du cluster et peut présenter les autres membres sans dépendre d’un serveur API ou d’une base de données centrale fixe. Les nœuds forment un maillage ; aucun membre n’est coordinateur permanent.

## Deux classes d’état

### Présence et mesures éphémères

- Les membres échangent des signaux de présence et des mesures par gossip pair-à-pair.
- Le signal inclut l’identité stable du membre, un compteur d’incarnation monotone, un numéro de séquence et les observations courantes (rôles, capacités, charge, dernier contact).
- Un signal transféré conserve son origine et sa séquence ; un relais ne rafraîchit jamais l’heure de présence d’un autre membre.
- La vue locale distingue `online`, `suspect`, `offline` et `unknown`. Les seuils sont configurables et les vues peuvent diverger brièvement pendant une panne réseau ; elles convergent après rétablissement du maillage.
- Les mesures fréquentes ne sont pas inscrites dans le journal durable de consensus.

### État durable et décisions exclusives

- Les changements de membres, rôles souhaités, bibliothèques, flows publiés, paramètres partagés, files et baux d’exécution passent par un journal répliqué en consensus Raft.
- Chaque serveur applique le journal validé dans son stockage local et sert les lectures depuis sa copie locale.
- Un chef temporaire peut être élu pour proposer les entrées ; il n’est ni une autorité permanente ni un point de stockage unique. Les écritures ne sont confirmées qu’après réplication au quorum.
- Les attributions de tâches et les baux portent une époque de consensus. Un nœud isolé ou devenu obsolète ne peut pas renouveler un bail ni publier un résultat après expiration de son époque.

## Quorum et partitions

- Les groupes votants utilisent un nombre impair de membres, 3 par défaut. Trois votes tolèrent la perte d’un membre ; cinq votes en tolèrent deux.
- Sans majorité, les nœuds conservent et servent leur dernier état validé, mais refusent les changements durables et suspendent les nouvelles attributions de tâches. Les traitements déjà engagés ne peuvent publier que tant que leur bail valide peut être confirmé.
- Les informations de présence restent consultables depuis les observations locales et sont marquées comme éventuellement divergentes. Une partition ne transforme jamais une observation périmée en présence confirmée.
- Une configuration à deux votes ne garantit pas de continuer après la perte d’un seul nœud ; l’installation avertit l’administrateur et recommande un troisième vote.

## Identité, découverte et rattachement

- Un cluster possède un identifiant et une autorité de confiance persistants, indépendants de l’adresse d’un serveur.
- DNS-SD et les adresses de démarrage servent à découvrir des pairs, pas à leur accorder la confiance.
- Le rattachement d’un serveur nécessite une approbation explicite et établit une identité cryptographique de pair. Les échanges ultérieurs sont authentifiés et chiffrés ; les secrets de nœud worker existants restent distincts des identités de serveur.
- Les changements de configuration des membres votants sont validés par consensus avec un protocole de jointure/sortie sûr. Un pair isolé ne peut pas s’ajouter lui-même au cluster.

## Données médias

Le consensus réplique les métadonnées, pas les fichiers vidéo, audio ou image. Chaque bibliothèque garde ses chemins et capacités d’accès propres à chaque nœud. La réplication des fichiers reste un choix de stockage séparé ; le transfert vers un worker continue d’utiliser les mécanismes déjà définis.

## Déploiement et migration

- L’installation de démarrage crée le premier pair et son identité de cluster ; une commande de rattachement approuvée ajoute ensuite chaque serveur.
- Chaque pair dispose d’un stockage local persistant. Il rejoint le quorum, reçoit un instantané puis rattrape le journal avant de servir comme membre à jour.
- La migration depuis le mode actuel importe la base existante sur le premier pair, puis initialise les autres à partir de son instantané validé. Elle ne fusionne pas silencieusement deux clusters indépendants.
- Le protocole, les écritures et les garanties de concurrence existants restent en vigueur jusqu’à ce que les tests de réplication, de reprise, de partition et de compatibilité de versions réussissent. Le mode partagé PostgreSQL demeure la seule option multi-serveur prise en charge durant la transition.

## Phasage

1. **Présence pair-à-pair** : découverte, confiance, gossip de présence/capacités et vue « Cluster » locale convergente.
2. **État durable** : consensus et réplication des identités, rôles, bibliothèques, flows et paramètres.
3. **Orchestration** : journal des exécutions, baux, époque de fencing, reprise après perte d’un pair et refus sûr sans quorum.
4. **Déploiement** : rattachement guidé, sauvegarde/restauration d’un pair, migration et diagnostics d’exploitation.

## Critères d’acceptation

- Après rattachement, chaque serveur présente tous les membres et leurs capacités depuis son état local.
- Un heartbeat envoyé à un seul pair finit par apparaître chez les autres ; les relais ne prolongent pas artificiellement le statut en ligne.
- La perte d’un pair sur trois laisse le cluster modifiable et capable d’attribuer des tâches sans doublon.
- La perte du quorum bloque les écritures durables et les nouvelles attributions sur tous les pairs ; le retour du quorum reprend la synchronisation sans réinitialiser les identités.
- Une partition réseau, un redémarrage et un rattachement concurrent ne créent ni deux membres de consensus pour la même identité, ni deux baux valides pour la même tâche.
- Les médias et les secrets de nœud ne sont jamais répliqués par le protocole de présence.

## Limites

La présence est cohérente à terme et peut diverger brièvement entre pairs ; aucun protocole de gossip ne peut donner une vérité instantanée commune pendant une partition. Les changements durables exigent un quorum.

## Choix technique initial

Le prototype évaluera `DotNext.Net.Cluster` et `DotNext.AspNetCore.Cluster` : le projet fournit Raft et HyParView pour le membership/gossip, cible .NET moderne et évite d’introduire un Raft développé par Encoding. Son `ConsensusOnlyState` en mémoire ne convient pas à la production ; le prototype doit utiliser un journal durable et vérifier la reconstruction après redémarrage. Les commandes doivent avoir des identifiants idempotents, car la bibliothèque ne fournit pas à l’application le contrôle des sessions clientes.

Le choix sera conservé uniquement si un prototype à trois nœuds démontre l’élection, la réplication persistante, le rattrapage après arrêt, la modification sûre de la membership et les comportements documentés sans quorum. Les projections locales SQLite/PostgreSQL ne sont jamais déclarées validées avant que leur entrée soit engagée au quorum.
