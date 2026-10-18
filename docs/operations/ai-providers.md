# Fournisseurs IA dans le cluster

## Installation

L'image embarque Codex CLI `0.163.0` et Claude Code `2.1.302`. Les arguments de construction `CODEX_CLI_VERSION` et `CLAUDE_CODE_VERSION` permettent une mise à jour explicite. L'installation utilise les paquets officiels npm ; aucun binaire tiers n'est versionné dans le dépôt.

Conserver le volume `Node:DataDirectory`, notamment les clés Data Protection et `credentials/ai-tools`. Ne pas monter le même profil de credentials en écriture sur plusieurs serveurs. Tous les serveurs doivent être approuvés dans le même cluster et joignables par les adresses du maillage. Les horloges doivent être synchronisées. Le mode historique de maillage 0049 est le chemin vérifié ; le mode optionnel `PeerCluster` Raft n'est pas couvert par les scénarios IA distribués de cette branche.

Ouvrir **Paramètres → Intégrations → IA & fournisseurs** sur le serveur qui doit héberger une connexion :

- Codex : **Associer mon compte**, ouvrir le lien fourni par la CLI et valider son code. Le compte doit autoriser ce mode de connexion.
- Claude : générer un jeton avec `claude setup-token` sur votre poste, puis le saisir dans le champ secret. Le contrôle de statut vérifie la reconnaissance locale, pas une requête au modèle.
- Ollama ou API compatible : enregistrer l'adresse et le modèle sur ce serveur. La clé d'API est protégée localement ; laisser le champ vide la conserve.

Choisir le moteur par défaut puis enregistrer. Les nœuds répondants et leurs fournisseurs sont listés sous la configuration. Les connexions déclarées ne garantissent ni le quota disponible ni la réponse du modèle.

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

Les checkpoints et les marqueurs locaux d'essai ne sont pas supprimés automatiquement : définir leur politique de rétention avant une utilisation intensive. Les checkpoints transitent chiffrés dans le journal du maillage ; les secrets de connexion n'y transitent pas.

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
