# 0071 — Outils IA, authentification réelle et sessions applicatives partagées

Statut : implémenté. Remplace les commandes et le device flow générique initialement décrits ici. Complète 0049, 0054, 0064 et 0072.

## Connexion des fournisseurs

Les comptes ne sont plus associés par un code généré par Encoding. Codex fournit le lien et le code à partir de `codex login --device-auth`. La sortie est suivie pendant l'exécution, avec nettoyage ANSI et extraction des instructions attendues. Claude utilise `claude auth login` : Encoding récupère le lien HTTPS officiel émis par la CLI, le présente à l'administrateur et transmet au processus le code de retour saisi après la connexion navigateur. Le lien et le code restent en mémoire pour la session, ne sont ni journalisés ni persistés. Les deux sessions sont bornées à quinze minutes, réutilisées tant qu'elles attendent, annulables et arrêtées à expiration. Le succès n'est confirmé qu'après la fin du processus et un contrôle positif avec `codex login status` ou `claude auth status`.

Claude utilise un jeton généré par l'administrateur sur son poste via `claude setup-token`. Encoding le protège avec ASP.NET Data Protection et le transmet uniquement au processus Claude via `CLAUDE_CODE_OAUTH_TOKEN`. `claude auth status` contrôle la reconnaissance locale des identifiants ; cela ne garantit ni la validité distante du jeton ni les quotas. Un remplacement refusé préserve le secret précédent.

Les répertoires `CODEX_HOME` et `CLAUDE_CONFIG_DIR` sont propres à chaque fournisseur et réutilisés pour connexion, statut, inférence et déconnexion. Le répertoire est sous `AiTools:CredentialsDirectory`, ou par défaut sous `Node:DataDirectory/credentials/ai-tools`. Les secrets et les marqueurs d'essai restent locaux et hors du maillage. Un ancien fichier `credentials.json` n'est jamais une preuve de connexion.

Les commandes de production sont `codex exec --json --ephemeral` et `claude -p --output-format stream-json --verbose --no-session-persistence`. Le prompt passe par stdin. Claude est appelé sans outils, sans serveurs MCP hérités, sans skills et sans sources de réglages projet/utilisateur. Codex désactive les outils shell et unified-exec, utilise le sandbox read-only et refuse les demandes d'approbation interactives. Les sorties sont bornées ; l'annulation et le délai maximal arrêtent l'arbre de processus. Aucune CLI absente ne produit de réponse simulée, et une génération de flow échouée ne retourne plus de flow synthétique comme résultat IA.

Sur Linux, le lancement réutilise le confinement Landlock partagé avec les scripts du worker, déplacé sans changement de comportement dans `Encoding.Core.Runtime`. Le profil IA est accessible en écriture ; les exécutables, bibliothèques, certificats et fichiers de configuration OpenSSL système sont accessibles en lecture. Les données d'Encoding et les médias extérieurs au profil ne sont pas accordés. Le dossier d'installation npm se règle avec `AiTools:RuntimeDirectory` (défaut `/opt/encoding/ai-cli`). Sans Landlock, la CLI refuse de démarrer ; il n'y a pas de repli de production qui supprime cette protection. Landlock borne ici le système de fichiers, pas les domaines réseau.

## Accès depuis tous les nœuds

Un serveur expose ses fournisseurs configurés ou ses identifiants CLI reconnus. Les autres serveurs peuvent acheminer leurs demandes vers lui. Les workers disposent aussi de `/api/v1/runtime/nodes/{nodeId}/ai/generate`, protégé par leur jeton de nœud existant. Un worker sans API locale peut consommer un fournisseur distant ; l'hébergement d'une connexion CLI dans cette implémentation nécessite la fonction serveur sur le nœud concerné.

Le transport IA réutilise la découverte du maillage et les identités RSA admises du cluster. `/api/v1/cluster/ai/exchange` ne dépend pas d'une session navigateur : chaque requête et réponse est signée et son contenu est chiffré/authentifié par AES-GCM avec la clé de données du cluster. L'identité, la fenêtre temporelle, le cluster et l'identifiant de réponse sont contrôlés. Ce canal n'offre aucune opération d'ajout ou de récupération de secrets.

Les adresses, modèles HTTP et secrets de connexion restent locaux au serveur d'exécution. Le moteur par défaut, le délai maximal et la taille des lots sont partagés via les registres du maillage. La réponse publique de configuration renvoie seulement `hasOpenAiApiKey`, jamais la clé.

## Contexte et reprise

Le contexte est celui d'Encoding, indépendant des sessions internes des CLIs : fournisseur, consignes système, demandes identifiées et tours terminés. Les contenus sont chiffrés et écrits dans les registres réconciliés avant de confirmer le succès au client. La limite d'un checkpoint est de 512 Kio ; une nouvelle session est nécessaire au-delà. Les artefacts média ne sont pas copiés dans ces checkpoints.

Chaque session a un coordinateur stable, inscrit dans son identifiant `peerId.sessionId`. Les requêtes reçues ailleurs sont acheminées vers lui. Il sérialise les tours, conserve la demande en attente et le délai de la tentative avant de l'envoyer. Une réponse déjà validée pour le même `requestId` est retournée sans nouvelle inférence ; réutiliser cet identifiant avec un autre prompt est refusé. Les tentatives ont également un identifiant durable propre à l'exécuteur, afin de ne pas relancer une tentative rejouée après redémarrage.

Si un exécuteur devient indisponible, le coordinateur essaie au maximum trois nœuds configurés pour le même fournisseur. Après une perte de transport ambiguë, il attend l'expiration de la tentative précédente avant d'en démarrer une autre. Si aucun fournisseur n'est disponible, la demande reste enregistrée en attente et peut être réessayée avec les mêmes identifiants. Au redémarrage du coordinateur, une tentative dont le délai n'est pas écoulé n'est pas immédiatement relancée.

Si le coordinateur est indisponible, aucun autre serveur ne reprend silencieusement son identité : sans consensus, une partition réseau ne prouve pas sa disparition. L'administrateur peut créer explicitement une nouvelle session depuis le dernier checkpoint reçu, via `/api/v1/tools/ai/sessions/{id}/fork` ou depuis l'assistant de flows. Le fork conserve les tours validés et exclut la tentative sans résultat confirmé ; il attend son délai s'il en existe un. Le registre se réconcilie de façon éventuelle : un serveur isolé peut ne pas encore avoir le dernier checkpoint.

Ce protocole évite de confirmer plusieurs fois le même tour applicatif. Il ne garantit pas une facturation exactement une fois chez un fournisseur : une requête déjà acceptée peut continuer à consommer du quota après une coupure. Les horloges des nœuds doivent être synchronisées. La réconciliation ne constitue pas une sauvegarde synchrone avant chaque réponse.

## Interface et exploitation

La page « IA & fournisseurs » distingue le moteur par défaut de la fiche sélectionnée. Une seule configuration est affichée ; les paramètres avancés sont repliés. Les boutons utilisent les variantes Material du socle et les secrets restent masqués. Le dialogue d'association affiche le lien Claude émis par la CLI et, lorsque celle-ci l'attend, transmet le code de retour sans l'enregistrer. Le statut de configuration déclaré par un nœud n'est pas présenté comme une sonde distante du modèle. La liste montre les pairs répondants ; l'absence d'un pair n'efface pas sa configuration.

Les actions de connexion, réglage et déconnexion requièrent l'administration. Les lectures restent soumises à l'authentification de gestion. Les sessions et leur contexte sont réservés aux administrateurs, aux workers authentifiés et aux pairs admis. Le bouton et l'endpoint de simulation sont supprimés. Les changements non enregistrés sont protégés au départ de la page et lors du changement d'onglet.

Voir [la procédure d'exploitation](../operations/ai-providers.md) pour les versions embarquées, les API, les limites et la validation.

Sources : [Codex CLI](https://developers.openai.com/codex/cli/reference), [Claude CLI](https://code.claude.com/docs/en/cli-reference), [authentification Claude](https://code.claude.com/docs/en/authentication).


## Évolution : historique et sessions natives éphémères

Les CLIs ne conservent plus de conversation native locale pour les inférences. Le contexte applicatif reste l’autorité pour la reprise distribuée ; aucun rattachement à un Projet web fournisseur n’est supposé. Les sorties structurées doivent confirmer explicitement la terminaison avant succès.

Les tours applicatifs peuvent désormais inclure la consommation observée et leur date de validation. Le checkpoint conserve aussi le modèle demandé, le périmètre optionnel (opération, bibliothèque, flow, média, exécution), la date de mise à jour et la dernière erreur contrôlée. Modèle et périmètre sont immuables pour empêcher la réutilisation d’un contexte sur un autre média. Les champs ajoutés sont optionnels pour relire les checkpoints existants ; une ancienne session sans périmètre reste utilisable sans périmètre.

L’historique administrateur pagine les registres chiffrés existants, sans nouveau stockage ni autorité centrale. Les résumés n’exposent pas les prompts. L’état distingue un bail actif, une demande en attente de reprise et un tour terminé. Les compteurs présentés concernent uniquement les tours validés, avec valeurs absentes explicites et sans estimation d’un quota restant. Voir la procédure d’exploitation pour les limites.


## Quotas de comptes CLI

Une sonde locale bornée réutilise le lanceur confiné existant avec un canal stdin/stdout bidirectionnel. Codex emploie la lecture de compte App Server ; Claude emploie le contrôle expérimental `get_usage` du SDK publié, sans prompt et sans scan d’historique (`skip_behaviors`). Le contrat partagé distingue fenêtres disponibles, indisponibilité et observation ancienne, avec dates d’observation et de contrôle. Le pourcentage restant est le complément du pourcentage fournisseur et ne définit pas un budget en jetons.

Les observations sont en mémoire, propres à la connexion de chaque nœud, et ne sont pas des registres réconciliés. Les serveurs les consultent via le transport chiffré et signé commun ; le panneau admin les conserve séparément sans somme inter-nœuds. Le cache d’une minute limite les sondes, les changements de connexion l’invalident, et une panne conserve une observation explicitement ancienne. Les fenêtres expirées ne deviennent pas des quotas pleins. La disponibilité de Claude dépend de la version et des scopes de la connexion ; aucune commande interactive ni extraction de secrets ne sert de repli.
