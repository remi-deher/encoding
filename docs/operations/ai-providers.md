# Fournisseurs IA dans le cluster

## Installation

L'image embarque Codex CLI `0.163.0` et Claude Code `2.1.302`. Les arguments de construction `CODEX_CLI_VERSION` et `CLAUDE_CODE_VERSION` permettent une mise à jour explicite. L'installation utilise les paquets officiels npm ; aucun binaire tiers n'est versionné dans le dépôt.

Conserver le volume `Node:DataDirectory`, notamment les clés Data Protection et `credentials/ai-tools`. Ne pas monter le même profil de credentials en écriture sur plusieurs serveurs. Tous les serveurs doivent être approuvés dans le même cluster et joignables par les adresses du maillage. Les horloges doivent être synchronisées. Le mode historique de maillage 0049 est le chemin vérifié ; le mode optionnel `PeerCluster` Raft n'est pas couvert par les scénarios IA distribués de cette branche.

Ouvrir **Paramètres → Intégrations → IA & fournisseurs** sur le serveur qui doit héberger une connexion :

- Codex : **Associer mon compte**, ouvrir le lien fourni par la CLI et valider son code. Le compte doit autoriser ce mode de connexion.
- Claude : **Se connecter**, ouvrir le lien fourni par `claude auth login` dans votre navigateur et terminer la connexion. Si la CLI demande un code de retour, le coller dans le dialogue pour le transmettre au nœud. Le contrôle de statut vérifie l’état local de la CLI, pas une requête au modèle.
- Ollama ou API compatible : enregistrer l'adresse et le modèle sur ce serveur. La clé d'API est protégée localement ; laisser le champ vide la conserve.

Choisir le moteur par défaut puis enregistrer. Les nœuds répondants et leurs fournisseurs sont listés sous la configuration. Les connexions déclarées ne garantissent ni le quota disponible ni la réponse du modèle.

Dans **Usages et modèles**, l’assistant et les opérations sur les flows peuvent hériter du moteur par défaut ou choisir un fournisseur distinct. Le modèle est celui configuré pour cette connexion. La règle **local uniquement** impose Ollama sur une adresse de boucle locale et interdit le routage vers un autre nœud ou fournisseur. Sans cette règle, les questions et ressources de l’assistant peuvent transmettre des définitions de flows, des métadonnées de nœuds et bibliothèques, ainsi que des événements et journaux d’exécution pouvant contenir des chemins. Les sous-titres ne sont pas joints comme source dédiée par ce parcours. Vérifiez le fournisseur effectif avant d’y joindre des données sensibles.

L’utilisateur peut sélectionner jusqu’à trois outils MCP de lecture : liste des flows, détail d’un flow, nœuds, exécutions récentes et vue des bibliothèques. Le modèle peut aussi demander un seul outil supplémentaire dans un format structuré. Le backend accepte uniquement cette liste blanche, valide les arguments et réutilise les handlers MCP en lecture seule. Une réponse de préparation invalide ne déclenche aucun outil. La préparation puis la réponse finale peuvent chacune consommer un appel fournisseur. La taille du contexte reste bornée. Une génération ou modification de flow reste une proposition : **Appliquer à l’éditeur** et **Rejeter** enregistrent la décision dans l’activité. Les administrateurs peuvent publier le brouillon attaché, supprimer un flow attaché, demander l’annulation d’une exécution attachée ou mettre en file les fichiers actuellement détectés d’une bibliothèque attachée. Chaque action est préparée par le serveur et confirmée dans l’interface avec un résumé signé, expirant après cinq minutes. Publication vérifie la révision du brouillon ; suppression et annulation vérifient la version/révision métier ; le lancement vérifie l’affectation et l’empreinte de la liste de fichiers. Les identifiants d’exécution déterministes rendent le lancement confirmé sans doublon lors d’une reprise.

Sur Linux, Landlock doit être disponible. Le lancement refuse explicitement de s'exécuter sans confinement. Pour une installation CLI hors image, configurer au besoin `AiTools:RuntimeDirectory` pour accorder en lecture le répertoire d'installation ; la fonction serveur doit pouvoir exécuter l'outil. Les binaires peuvent être choisis par `AiTools:Executables:codex-cli` et `AiTools:Executables:claude-cli`.

## Demande et reprise

L'API administrateur `POST /api/v1/tools/ai/generate` accepte :

```json
{
  "provider": "codex-cli",
  "prompt": {
    "systemPrompt": "Réponds en français.",
    "userPrompt": "Résume ce texte…",
    "requestId": "11111111-1111-4111-8111-111111111111"
  }
}
```

La réponse contient `sessionId` et `requestId`, y compris quand aucun exécuteur n'est disponible. Conserver les deux pour réessayer exactement la même demande. Pour le tour suivant, garder le `sessionId` et choisir un nouveau `requestId`. Les consignes système et le fournisseur restent fixes dans une session. Une première requête avec le même `requestId` sur le même coordinateur retrouve la même session ; si aucun premier accusé n'est reçu, éviter de lancer simultanément cette requête sur plusieurs coordinateurs.

`GET /api/v1/tools/ai/sessions/{id}` lit le checkpoint présent sur ce serveur. S'il n'est pas encore réconcilié, la réponse est 404. Si le coordinateur est perdu, `POST /api/v1/tools/ai/sessions/{id}/fork` crée une nouvelle session avec les tours validés reçus. Le client doit conserver le nouvel identifiant. Les tours non confirmés ne sont pas copiés. L'assistant de flows présente cette reprise après un échec.

Une tentative est bornée par le délai configuré, entre 10 et 600 secondes. Un remplacement ambigu attend le délai précédent, avec une marge de cinq secondes ; au maximum trois exécuteurs sont essayés par appel. Si le coordinateur redémarre, attendre l'expiration du bail affiché avant de réessayer. Un échec peut avoir consommé du quota chez le fournisseur.

Les marqueurs locaux d'essai ne sont pas supprimés automatiquement. Les checkpoints transitent chiffrés dans le journal du maillage ; les secrets de connexion n'y transitent pas.

Les conversations terminées et l’activité IA expirent automatiquement. Les valeurs par défaut sont 90 jours pour les conversations (instantanés de contexte compris) et 30 jours pour l’activité. Les administrateurs peuvent régler ces durées dans **Paramètres → IA → Paramètres avancés du cluster** ou via `AiTools:AssistantConversationRetentionDays` et `AiTools:AssistantActivityRetentionDays` (1 à 3 650 jours) au démarrage. Le réglage enregistré est répliqué au cluster. Chaque nœud publie un tombstone dans le registre répliqué ; la passe s’exécute toutes les heures et ignore une conversation qui possède encore une demande en attente.

Le journal de réconciliation reste append-only : les tombstones retirent le contenu des registres actifs et des API, mais ne réécrivent pas les anciens événements chiffrés déjà présents dans l’historique local ou celui d’un pair hors ligne. Cette rétention applicative n’est donc pas une purge cryptographique garantie des sauvegardes/journaux. L’interruption d’une demande arrête l’attente locale lorsqu’elle est contrôlable ; un fournisseur distant peut avoir déjà accepté le travail. L’activité indique alors que le résultat distant reste incertain et la reprise attend l’expiration du bail.

Le corpus de scénarios de référence se trouve dans docs/testing/ai-assistant-eval-cases.json. Il couvre l’ancrage aux sources, les injections dans les descriptions et journaux, les appels et arguments d’outils, les conflits de version, l’annulation et l’incertitude sur les coûts. Il sert de base à l’évaluation des fournisseurs ; la suite automatisée vérifie actuellement sa structure et sa couverture, pas la qualité sémantique des réponses d’un modèle.

## Vérifications de la branche

- Tests .NET des commandes via exécutables contrôlés, statuts, absence de simulation, protection des secrets et refus sans Landlock.
- Deux bases SQLite indépendantes : admission signée, routage vers un fournisseur distant, continuation depuis un autre serveur, idempotence, redémarrage, expiration de bail et réconciliation de contexte chiffré.
- Tests Angular de la page et de la fenêtre d'association ; Playwright sur ordinateur et mobile, thèmes clair/sombre et audit axe WCAG AA.

Aucun compte personnel n'est associé par les tests. L'environnement cloud de développement ne fournit pas Landlock : les scénarios d'authentification utilisent un lanceur de test contrôlé ; un test distinct vérifie que le lanceur de production refuse ce cas. L'exécution réelle confinée des deux CLI et la construction complète de l'image restent à vérifier sur un hôte Linux compatible avant déploiement. Les capacités de traitement IA des blocs média au-delà de l'API de génération sont hors de cette modification.

### Diagnostiquer une connexion CLI

Dans **Paramètres → Système & Apparence → Journaux système**, filtrer la catégorie
`Encoding.Api.Tools.DeviceCodeAuthService` sur le nœud où la connexion a été démarrée.
Les tentatives Codex possèdent un identifiant de corrélation et indiquent le démarrage,
la durée, le code de sortie, les tailles stdout/stderr et la reconnaissance de l’URL
et du code. Les sorties brutes, codes de connexion et jetons ne sont pas journalisés.
Les avertissements et erreurs restent dans l’historique local persistant.

Le délai d’attente du premier code reste de 30 secondes. L’interface distingue une
CLI silencieuse, une sortie sans invitation reconnue et une sortie prématurée du
processus. Une sortie sans code avec un statut 0 ne valide pas une connexion.
Pour Claude, un contrôle local qui échoue ou dépasse 10 secondes est affiché
séparément d’un jeton non reconnu. Le contrôle local ne prouve pas l’accès au modèle
ni la disponibilité du quota chez le fournisseur.

Le lecteur de journaux accepte les réponses HTTP historiques sous forme de tableau
ainsi que l’enveloppe `entries` pour les mises à jour progressives des nœuds. Le flux
SSE écoute les événements nommés `log`, se reconnecte après interruption et déduplique
les événements rejoués. Les journaux restent propres au nœud consulté.
