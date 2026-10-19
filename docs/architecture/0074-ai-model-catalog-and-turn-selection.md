# 0074 — Catalogue IA observé et sélection par tour

Statut : implémenté. Complète [0071](0071-ai-cli-tools-and-headless-authentication.md).

## Autorité et découverte

Le frontend ne contient aucun catalogue métier de modèles ou de niveaux de raisonnement. Chaque serveur interroge les fournisseurs connectés avec son profil local, puis expose les observations via le transport IA signé et chiffré existant. Les secrets ne sont pas répliqués. Le choix d’une connexion référence l’identité du serveur API (`NodeId`), distincte du nœud ressource joint à la conversation.

- **Codex** : `app-server`, initialisation JSON-RPC, puis `model/list` paginé. Les identifiants, libellés, modèle par défaut, efforts autorisés et effort par défaut proviennent du catalogue ; les modèles cachés sont exclus. Aucune commande TUI `/model` ni génération n’est nécessaire.
- **Claude Code** : initialisation du protocole de contrôle stream-json. La réponse `models` publie les identifiants, libellés et, selon la version, `supportedEffortLevels`. Aucune entrée utilisateur n’est envoyée. L’absence de capacités ne produit aucune liste de niveaux supposée.
- **API OpenAI compatibles et Ollama** : `GET /models` à partir de l’URL OpenAI compatible configurée. La liste standard ne décrit pas les capacités de raisonnement : elles restent explicitement inconnues. Il n’y a pas de déduction à partir d’un préfixe de modèle. Ollama n’est pas configuré sur une nouvelle installation ; les configurations déjà enregistrées restent conservées.

Les sondes réutilisent les profils, le confinement et le protocole borné existants. Délai de 15 secondes par fournisseur, réponse maximale de 1 Mio, pages Codex bornées à 16. Le cache local d’une minute est invalidé lors des changements de connexion et distingue `available`, `unavailable` et `stale`, avec dates d’observation et de contrôle. Un catalogue ancien peut être consulté par l’administrateur mais ne permet pas une nouvelle sélection. Une incompatibilité de CLI ne déclenche aucun repli interactif, modèle fictif ou inférence de découverte.

Sources des formats : [Codex Model](https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/schema/typescript/v2/Model.ts), [Codex ModelListResponse](https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/schema/typescript/v2/ModelListResponse.ts), type `ModelInfo` et méthode `supportedModels()` du [SDK Claude Agent](https://platform.claude.com/docs/en/agent-sdk/typescript). Les protocoles effectivement disponibles dépendent des versions embarquées et de la connexion.

## API et désactivations

- `GET /api/v1/tools/ai/models?refresh=true` : catalogue administrateur, séparé par nœud et fournisseur, avec politique de modèles désactivés.
- `PUT /api/v1/tools/ai/models/policy` : `{ provider, model, enabled }`, réservé à l’administrateur.
- `GET /api/v1/tools/ai/assistant/options?refresh=true` : sélection utilisable par l’assistant, après filtrage des modèles désactivés, des catalogues anciens et de la politique local uniquement.

La politique d’activation est un réglage partagé par couple fournisseur/identifiant de modèle. Un registre chiffré distinct par couple évite que deux changements sur des modèles différents s’écrasent entre pairs. La réconciliation reste éventuelle suivant 0049 ; une désactivation ne stoppe pas une inférence déjà engagée. Aucune règle ne modifie silencieusement le modèle d’une conversation existante. L’absence de modèle actif bloque un nouveau message jusqu’à un choix valide. Les réglages de découverte restent locaux et ne sont pas enregistrés comme des valeurs métier permanentes.

## Conversations et reprise

Le fournisseur, le modèle, l’effort demandé et la connexion peuvent changer **entre deux messages** de l’assistant. Ils sont validés contre les capacités observées et enregistrés avec chaque tour ; la conversation conserve la dernière sélection. Les autres usages de sessions IA gardent leurs invariants de fournisseur, modèle et effort fixes, ainsi que de consignes et de périmètre immuables.

Les messages d’une conversation sont acheminés à son coordinateur stable avant de préparer le contexte et de lancer le planificateur. Le coordinateur sérialise l’ensemble du parcours, et le relais signé conserve l’identité du propriétaire. L’annulation d’une requête relayée revient au même coordinateur. Aucun serveur central permanent n’est ajouté.

Un tour en attente, son identifiant, sa sélection, ses ressources et son prompt préparé restent fixes pour une reprise. Un second message ne remplace pas une demande en cours. Les baux et les revendications durables d’exécution existants restent applicables. Un résultat réseau incertain conserve l’identifiant et la sélection ; un rejet de validation permet de corriger le choix. Rejouer un tour terminé renvoie son résultat sans réexécution et sans remettre la conversation sur son ancien modèle.

Le backend distingue le modèle demandé du modèle effectif présent dans la réponse fournisseur (`AiUsage.Model`). Claude et les API compatibles peuvent communiquer le modèle effectif. La sortie JSON Codex actuellement parsée peut ne pas le communiquer : l’interface affiche alors « Modèle demandé » et « Modèle effectif non communiqué ». L’effort est affiché comme demandé ; aucune confirmation d’effort effectif n’est inventée.

## Compatibilité et validation

Les nouveaux champs de checkpoint sont optionnels : un ancien tour reste lisible avec un modèle non communiqué. Un ancien serveur ne publiant pas de catalogue n’est pas offert comme connexion pour une nouvelle sélection explicite ; le routage historique des autres usages garde sa compatibilité. Mettre à jour tous les serveurs avant d’utiliser les nouveaux relais de conversation ; un coordinateur incompatible ou absent est signalé, sans exécution concurrente sur un autre serveur.

Les tests couvrent les protocoles sans génération, la pagination, les capacités propres à un modèle, le cache et les erreurs sans secret, les politiques réconciliées, le routage vers la connexion choisie, les métadonnées historiques, le relais et son annulation, les reprises et rejets de validation, et les parcours Angular sur ordinateur/mobile avec contrôle WCAG AA. Les fixtures ne prouvent pas les droits d’inférence d’un compte réel : ceux-ci sont vérifiés par le fournisseur à l’exécution, avec ses erreurs et quotas.
