# 0015 — Runtime des flows et archives portables

## Décision

Le registre existant reste l’autorité des états et événements. Les migrations 0010 et 0011 ajoutent les brouillons et leurs sauvegardes, les travaux média, les réservations, les checkpoints et artefacts. Un worker exécute une version publiée précise du graphe validé.

## Édition et restauration

Le catalogue des blocs vient des manifestes backend. Chaque instance conserve les versions exactes du plugin, du composant et du schéma. Un brouillon accepte des champs obligatoires absents et des blocs non raccordés, en conservant les contrôles des valeurs renseignées et de structure. Ses révisions sont indépendantes des versions publiées et utilisent un contrôle de concurrence. Publier valide le graphe et supprime uniquement le brouillon dont la révision a été confirmée.

Les révisions des brouillons et les versions publiées sont sauvegardées par une file transactionnelle dans le volume séparé. Une sauvegarde existante différente est conservée et signalée en échec.

Le tableau propose Importer JSON/ZIP, Exporter JSON sur une ligne et Exporter ZIP sur une sélection. Le ZIP contient toutes les versions publiées et le brouillon actuel de chaque flow sélectionné. La comparaison permet de sélectionner deux versions publiées et montre les différences de blocs, configuration et connexions.

Un brouillon utilise l’enveloppe {"kind":"draft","draftRevision":3,"definition":{}}. Une version publiée utilise directement FlowDefinition. L’import valide tous les documents avant une transaction unique. Une version identique est idempotente ; un document existant différent provoque un conflit sans écrasement. Restaurer d’anciennes versions ne diminue pas la version courante.

Limites : 100 flows sélectionnés, 1 000 documents, 20 Mio compressés et décompressés, 1 Mio par document importé. Les chemins ZIP ne sont jamais extraits sur disque.

## Attribution et reprise

POST /api/v1/media-executions reçoit bibliothèque, chemin relatif et version publiée. Le tableau fournit cette action via le formulaire commun. Les montages et accès des bibliothèques restent explicitement configurés.

Les serveurs attribuent les travaux depuis PostgreSQL avec verrous transactionnels et SKIP LOCKED, sans serveur permanent. Le worker doit être présent, avoir une place disponible, les outils et encodeurs requis, les versions exactes des plugins et un accès direct ou par transfert éligible.

Le bail dure 60 secondes, renouvelé toutes les 15 secondes. Une réservation unique porte sur bibliothèque/chemin ; chaque reprise incrémente l’époque. Les étapes, transferts, renouvellements et publications vérifient cette époque et l’expiration. Checkpoint et événement sont enregistrés dans la même transaction.

Le moteur suit une seule sortie à chaque étape. Les convergences sont séquentielles. Une sortie non raccordée termine le parcours ; une erreur non traitée échoue. Les terminaux supportent réussite, inchangé, échec et report. Un report termine la tentative et en crée une nouvelle, différée de 1 à 1 440 minutes. L’annulation arrête les processus ; une annulation abandonnée est clôturée après expiration.

Les handlers Base réexécutables reprennent depuis le dernier checkpoint. Un script ou plugin interrompu avant son checkpoint a un effet incertain : échec et relance explicite, sans répétition automatique de l’effet.

## Outils et intégrité

L’image worker installe FFmpeg/ffprobe, MKVToolNix, MediaInfo, ImageMagick, Python et Node.js. Les adaptateurs construisent des arguments sans shell, bornent les réponses et arrêtent les arbres de processus à l’annulation. Les versions et encodeurs détectés restent distincts des descripteurs.

Les handlers Base couvrent inspection, rapports, transcodage, normalisation, redimensionnement, remux, pistes, propriétés Matroska, chapitres, pièces jointes, extraction et miniatures. Un plan regroupe les opérations FFmpeg compatibles ; l’outil choisi à sa préparation contraint les actions ajoutées.

Les scripts accèdent à context et variables et peuvent retourner {"outputId":"success","variables":{},"candidate":"result.mkv"}. Les conditions JavaScript utilisent variables et context.metadata, avec durée et génération de code bornées.

La copie originale sert d’instantané avant. Une entrée spécialisée analyse immédiatement ; une entrée générique laisse exécuter des scripts préparatoires puis impose le scan avant une opération média ou la fin. Une sortie candidate est analysée et validée : fichier non vide, pistes attendues, durée cohérente, décodage audio/vidéo complet. Les instantanés avant/après sont conservés dans l’historique. Les fichiers auxiliaires sont conservés et téléchargeables dans le détail d’exécution.

## Transfert et publication

Le nœud d’accès transfère une copie vérifiée par les endpoints WebDAV du runtime. Le worker télécharge dans son espace local. Les fichiers sourceFile sont des chemins relatifs déclarés dans le flow, transférés avec le même contrôle. Les transferts vérifient SHA-256, reprennent les téléchargements par Range et ne publient que des blobs complets.

Runtime:ArtifactDirectory configure le volume durable des artefacts. Tous les serveurs d’un cluster multi-hôte doivent partager ce stockage ; un volume Compose local n’établit pas une haute disponibilité entre machines. Aucun nettoyage automatique n’est activé.

Le nœud d’accès contrôle de nouveau la source, le résultat et le bail avant publication. Le résultat est publié atomiquement sans écrasement sous .encoding-results/<executionId>/result.<extension>. Une sortie déjà présente doit avoir la même empreinte. Le fichier original est conservé. Son remplacement est une politique de bibliothèque explicite, désactivée par défaut : voir [0030](0030-source-replacement-policy.md).

## Plugins en processus séparés

FlowPlugins:ManifestDirectory configure les manifestes supplémentaires sur les serveurs. Chaque worker configure Runtime:Plugins avec Id, Version, chemin absolu Executable, Arguments et éventuellement ReadOnlyPaths pour les dépendances. Les fichiers plugin doivent être installés hors des données privées du nœud.

Chaque appel lance un processus distinct. La description reçoit sur stdin {"type":"describe","contractVersion":1} et retourne PluginManifest. L’exécution reçoit {"type":"execute","contractVersion":1,"invocation":{}} et retourne FlowStepResult avec la même invocation et époque. Les réponses sont bornées et contrôlées. Un candidat est un chemin relatif dans l’espace temporaire ; l’hôte garde l’analyse et la publication.

Scripts et plugins utilisent Linux Landlock : écriture seulement dans leur espace temporaire, lecture des exécutables et dépendances autorisées, sans accès au jeton ni aux montages média. Si Landlock est indisponible, ces capacités ne sont pas annoncées et ces processus ne sont pas exécutés. Aucun repli sans restriction, ni conteneur dédié par plugin. Cette restriction n’isole pas complètement le réseau, les ressources ou tous les appels système ; les plugins restent installés par un administrateur.

## Socle partagé et validation

Le tableau commun gère les actions de liste et la sélection de fichier d’import. Le formulaire commun reconstruit ses contrôles quand la définition change et gère son état occupé. Le gabarit de suivi expose les fichiers produits. Encoding.Core.AtomicFilePublication mutualise la publication des sauvegardes, blobs et résultats avec synchronisation du dossier sous Linux.

Les compilations, le lint et le démarrage Docker sont contrôlés pour cette livraison. Les scénarios sur médias, multi-nœuds, de pertes de bail et de plugins tiers réels restent à valider selon TEST_RULES.md ; aucun test média n’est exécuté dans cette intervention.
