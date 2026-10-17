# 0059 — Plex : lecture, garde de publication et analyse

Statut : implémentée (sixième PR du plan « exécuter un flow FileFlows »). Plugin Plex 1.1.0.

## Besoin

Remplacer un fichier source (0030) pendant qu'il est lu dans Plex coupe la lecture. Le bloc « Lecture en cours dans Plex » existe déjà (0032) mais un flow ne peut pas protéger le remplacement, qui a lieu après la fin du flow, sur le nœud d'accès. Il faut aussi tenir compte de l'épisode suivant (une série enchaîne) et pouvoir demander l'analyse d'un fichier à Plex.

## Décision

### Lecture en cours (bloc existant étendu)

`plex-check-playback` (version de composant inchangée, 1.0.0) gagne deux champs facultatifs, sans effet pour les flows existants :

- `includeNextEpisode` : l'épisode précédent de la même saison compte comme une lecture de ce fichier ;
- `nextEpisodeMarginMinutes` : l'épisode précédent ne compte que s'il lui reste au plus cette durée (0 : pendant toute sa lecture ; durée inconnue : compte).

Le fichier est retrouvé comme pour `plex-check-media-indexed` (section, filtre `file` revérifié sur le chemin exact). Un seul sens est géré : lire l'épisode suivant ne retarde pas le précédent, et le passage d'une saison à l'autre n'est pas détecté.

### Analyse

Nouveau composant `plex-analyze-media` (sorties `success`, `notFound`, `error`) : `PUT /library/metadata/{ratingKey}/analyze`, sans attendre la fin (Plex termine seul). La clé vient du champ `ratingKey` ou, par défaut, du fichier traité via `pathMappings`.

### Garde de publication (point d'extension)

Le manifeste d'un plugin peut déclarer `publicationGuard` (`PluginPublicationGuard`) : un composant, la configuration fixe et les champs de plugin qui l'alimentent, le champ qui l'active, le champ d'attente maximale, et les sorties qui signifient « attendre » ou « autoriser ». Le registre valide cette déclaration au démarrage. Plex déclare `plex-check-playback` : `playing` attend, `idle` autorise.

Déroulement, dans l'ordre de 0030 :

1. Le résultat distinct est publié et revérifié (inchangé).
2. Le serveur a joint à la liste de transferts du nœud d'accès les gardes actives (`PublicationGuardProvider`) : seulement pour les publications avec politique de remplacement, seulement pour un plugin configuré dans le cluster et dont `guardReplacement` est vrai (vrai par défaut). Les valeurs effectives, secrets compris, suivent le même chemin que pour un worker.
3. Le nœud d'accès invoque le composant dans le processus isolé du plugin (`PublicationGuardRunner`), comme une étape de flow. `idle` autorise ; `playing` fait réinterroger toutes les 30 secondes, jusqu'à `guardMaxWaitMinutes` (30 par défaut, 720 au plus).
4. Autorisation : le remplacement se déroule comme avant. Attente épuisée, garde indisponible (plugin absent ou d'une autre version sur le nœud, Plex injoignable, jeton refusé, bibliothèque sans correspondance de chemin) ou sortie inattendue : le remplacement n'a pas lieu, le rapport `failed` en donne la raison, le résultat distinct reste publié et la source est intacte.

## Garanties

- Jamais d'écrasement sur doute : tout ce qui n'est pas une autorisation explicite est un refus.
- Pendant l'attente, le suivi du bail de la source continue (le transfert reste actif) ; l'annulation de l'exécution ou la perte du bail interrompt l'attente sans remplacer.
- Perte du nœud d'accès pendant l'attente : le bail expire, le transfert est repris, la garde est réévaluée depuis le début et `SourceReplacer.ResumeAsync` reconnaît une bascule déjà faite : pas de double bascule.
- Aucun secret n'apparaît dans les détails remontés ; l'exception de la garde n'est journalisée que par son type.

## Compatibilité

Plex passe en 1.1.0 ; les composants existants listent 1.0.0 comme version de plugin compatible, donc les flows existants restent valides. Un worker qui n'annonce pas `plugin/plex` 1.1.0 ne reçoit plus de flow qui utilise Plex (`docker-compose.yml` déclare 1.1.0). Un agent plus ancien ignore les gardes : pendant une mise à jour progressive, mettre à jour le nœud d'accès avant d'activer une politique de remplacement.

## Limites

- La garde s'applique à tous les remplacements de source du cluster tant que Plex est configuré ; il n'y a pas de réglage par bibliothèque.
- L'attente occupe un emplacement de transfert du nœud d'accès (au plus `guardMaxWaitMinutes`) ; le plafond n'est pas conservé en base : après perte du nœud, il repart de zéro.
- Les API Plex n'ont été vérifiées que contre un faux service ; le sandbox Landlock de l'invocation de la garde n'a pas été exercé ici (non disponible dans le conteneur de développement).

## Tests

`test_plugins.py` (Plex comme processus réel contre un faux service) : épisode précédent avec et sans option, marge, autres séries et épisodes suivants, règle `blocks_playback`, analyse par chemin et par clé. Runtime.Tests : `PublicationGuardsWaitAllowAndRefuseSafely` (autoriser, attendre puis autoriser, attente plafonnée, garde indisponible, sortie d'erreur, plusieurs gardes, annulation, transport JSON, catalogue et compatibilité 1.0.0) et `PublicationGuardsComeFromTheConfiguredPlugin` (SQLite : aucune garde sans configuration, valeurs par défaut, réglages, désactivation).
