# Corrections consécutives à l’incident du 27 octobre

Ce document distingue les changements de code des opérations de production. Aucune réparation des bases de production, modification de comptes réels, modification des ACL ou intervention TrueNAS n’est incluse.

## Changements implémentés

- `flow_draft_backups` est immuable dans la projection. Un instantané identique est acquitté sans UPDATE ; les champs locaux (`status`, échéances et vérification) restent intacts. La comparaison JSON ignore l’ordre des propriétés. Une définition différente pour la même clé conserve la ligne locale et l’événement en attente, avec `immutable_snapshot_conflict`. Ce conflit n’est plus retenté automatiquement : il exige une investigation et une résolution explicites ; aucun endpoint n’écrase l’instantané.
- La migration additive `0058_projection_diagnostics.sql` crée une table **locale, non réconciliée** de diagnostic. Elle ne contient ni payload, ni SQL, ni message d’exception. Le journal reste conservé. Les erreurs de contraintes, collisions de noms, dépendances manquantes et indisponibilités de stockage sont distinguées. Un avertissement est émis lors du premier échec ou d’un changement de cause, pas à chaque tentative.
- `/api/v1/cluster/reconciliation/status` conserve ses champs et ajoute `projections` et `attributionBlockers`. Le bandeau du cluster montre la cause, la table, la date du premier échec et l’action attendue. Le défaut de confirmation sortante d’un pair est explicite, même sans projection en attente. Les barrières d’attribution restent inchangées.
- Les plugins Plex, Sonarr, Radarr et Jellyfin présents sous `/opt/encoding/plugins` sont découverts depuis leurs manifestes. `Runtime:BundledPlugins:Enabled=false` les désactive tous ; `Runtime:BundledPlugins:<id>:Enabled=false` désactive une intégration. `Runtime:Plugins` continue de primer ; `Enabled=false` sur une déclaration explicite désactive cette déclaration. Une présence sur disque n’annonce aucune capacité avant le handshake existant sous Landlock. La configuration du service distant demeure obligatoire.
- À l’attribution, les champs obligatoires des configurations effectives des plugins référencés sont vérifiés avant le travail média. Une absence échoue explicitement avec le code `plugin_configuration_missing`, le bloc, le plugin et le champ concernés, sans leur valeur. Aucune branche ni protection n’est désactivée.
- Avant le traitement sur un accès source nécessitant l’écriture, le processus vérifie création, écriture, relecture, renommage et nettoyage dans un sous-dossier temporaire dédié. Si `.encoding-results` existe, la sonde utilise ce répertoire ; sinon elle vérifie sa racine future. Une nouvelle vérification a lieu à la publication. Aucun média existant n’est touché et aucun droit n’est accordé par la sonde.
- Les champs optionnels `verifyPublicationAccess` des baux et transferts sont additifs. Les anciens workers les ignorent : ils n’offrent donc pas ce contrôle préventif. Le contrôle ne garantit pas les droits futurs et ne vérifie pas toutes les ACL du remplacement de source ; les contrôles et gardes de publication restent nécessaires.
- Le pipeline reste lisible lorsque l’historique contient des étapes mais une définition de flow partielle. Les étapes gardent leur identité et leur état ; aucune complétion n’est inventée. Les messages de préparation/transfert et de publication sont distincts. Cela couvre un cas de HTTP 500 reproduit en test, sans prouver qu’il était la cause exacte du 500 de production.

## Travaux restant à implémenter ou à diagnostiquer

- Résolution guidée des collisions de comptes au rattachement, avec validation des renommages, sans fusion d’identités. Le diagnostic de collision est disponible ; aucun compte réel n’est renommé par ces changements.
- Cause exacte du second blocage `ready=0` après redéploiement : le nouveau diagnostic aide à distinguer les prérequis. La réception d’événements ne remplace pas la confirmation sortante du rattrapage. Aucun `ready=1` n’est forcé.
- Origine précise des demandes concurrentes de production et coordination avant création entre serveurs. Le départage et la quarantaine existants sont conservés. Le code actuel présente déjà la quarantaine dans les cartes d’activité.
- Identité d’accès personnalisée réellement isolée par bibliothèque/nœud : les accès directs et montages sont existants, mais la nouvelle sonde n’accorde pas de droits. Dans Docker, les groupes supplémentaires restent communs au processus entier. Ne pas présenter un champ UID/GID comme une délégation effective.
- Vérification de bout en bout de la segmentation préparation/encodage/relais et du maintien de la référence source. Les politiques existantes ne sont pas remplacées.
- Nouveau contrôle explicite rapide/approfondi, accélération CPU/GPU vérifiée, suivi du contrôle, réutilisation par empreinte, ADR et contrat de compatibilité. **Le décodage intégral obligatoire actuel reste actif** tant que cette évolution n’est pas mise en œuvre et testée sur tous les chemins.
- Observabilité complète des étapes de copie, validation et publication, erreurs structurées de bout en bout, distinction durable entre publication confirmée et échec d’actualisation d’une intégration.

## Vérifications

Les tests isolés ajoutés couvrent le rejeu identique/divergent, le statut local, le diagnostic persistant, les plugins livrés et leur désactivation, les prérequis de configuration sans secret, les sondes d’accès et les historiques partiels. Le test existant de redémarrage de la propriété vérifie aussi le diagnostic du pair non rattrapé. Les deux tests UI du bandeau vérifient la cause et le rattrapage manquant.

Les résultats d’exécution sont rapportés dans le bilan de la tâche ; ce document ne revendique aucune validation en production, GPU réel, TrueNAS, déploiement mixte ou traitement des épisodes personnels.

### Résultats locaux

- Compilation backend Release avec `-warnaserror` : réussie, zéro avertissement et zéro erreur.
- Suite runtime complète sur l’état intermédiaire : 514 réussis, 17 ignorés, aucun échec (5 min 35 s). Les modifications suivantes ont été couvertes par les validations ciblées ci-dessous.
- Dernière sélection Release incluant réconciliation, propriété, transferts et nouvelles régressions : 55 réussis, 1 ignoré, aucun échec.
- Lint frontend et compilation Angular : réussis. Deux avertissements de budget SCSS existants (navigation : 17 octets ; catalogue des nœuds : 2 octets).
- Bandeau de diagnostic : 2 tests réussis.
- Rejeu des sauvegardes : test supplémentaire contre la recapture en boucle d’un JSON sémantiquement identique avec un ordre de propriétés différent. Le protocole et les empreintes des événements entrants restent inchangés ; seule l’empreinte d’observation locale conserve la représentation réellement stockée.

Ces résultats ne ferment pas les travaux restants listés plus haut.
