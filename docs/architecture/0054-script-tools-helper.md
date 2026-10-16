# 0054 — Aide `tools` des scripts : lancer les outils média sous Landlock

Statut : implémentée (première PR du plan « exécuter un flow FileFlows »). Décision de Rémi : les scripts sont peu utiles s'ils ne peuvent pas invoquer d'outils, et prévoir chaque situation dans des blocs natifs n'est pas tenable.

## Constat

Un script `base-script` s'exécute sous Landlock (0015) : écriture limitée à l'espace temporaire de l'exécution, lecture de `/usr` (donc exécution de `ffmpeg`, `ffprobe`, `mkvmerge`, `mkvextract`, `mkvpropedit`, `mediainfo`, `magick`), aucun accès aux montages média ni au jeton. Un script pouvait donc déjà lancer ces outils sur la copie de travail (`current.<ext>` et la source téléchargée dans son dossier), mais sans aide : chercher l'exécutable, éviter le shell, borner les sorties et garder une trace des commandes étaient à la charge de chaque script.

## Décision

Un script qui active `useTools` reçoit l'objet `tools` (JavaScript et Python), sans élargir le bac à sable :

- `tools.run(nom, arguments, options)` lance l'outil sans shell dans le dossier de travail du script, sous le même Landlock que le script. Retour : `exitCode`, `ok`, `stdout`, `stderr` (sorties bornées à leur fin). `ok` vaut vrai pour le code 0, et pour le code 1 des outils `mkv*` (avertissements). Options : `timeoutSeconds` (6 h par défaut) ; JavaScript : `input` ; Python : `timeout_seconds`, `input`.
- `tools.path(nom)` retourne le chemin résolu d'un outil de la liste, sinon `null`. `tools.allowed` liste les noms.
- Liste de noms : `ffmpeg`, `ffprobe`, `mkvmerge`, `mkvextract`, `mkvpropedit`, `mediainfo`, `magick`. Un autre nom lève une erreur claire.
- Chaque appel écrit une ligne `[tools] <outil> <arguments> -> <code> (<ms> ms)` sur la sortie d'erreur du script ; elle est conservée dans le journal de commandes de l'étape (0028).
- La durée maximale du bloc (`timeoutSeconds`) passe de 3 600 s à 86 400 s, car un remux ou un encodage dure souvent plus d'une heure. L'hôte arrête toujours l'arbre de processus à l'expiration ou à l'annulation.

Sans `useTools`, le programme généré est celui d'avant (aucun identifiant ajouté aux scripts existants). Seul changement : une exception JavaScript non rattrapée écrit sa trace sur la sortie d'erreur avant de sortir avec le code 1.

## Ce que l'aide n'est pas

La liste blanche n'est **pas** une frontière de sécurité : un script peut lancer tout exécutable lisible sous Landlock sans passer par `tools`. La frontière reste Landlock. Les arguments choisis par le script ne peuvent atteindre ni les montages média ni les données du nœud. Les outils ne reçoivent pas non plus les périphériques GPU : `/dev/dri` n'est pas autorisé dans le bac à sable, donc un encodage matériel (QSV, VAAPI, NVENC) depuis un script reste impossible ; il passe par `base-transcode-video`. Étendre le bac à sable aux périphériques GPU demanderait sa propre décision.

## Compatibilité de cluster

Un worker dont le bac à sable est disponible annonce la capacité `script-tools` (version `1`). Un flow dont un `base-script` a `useTools: true` n'est attribué qu'à un worker qui l'annonce (`RuntimeEligibility`). Un flow sans `useTools` reste exécutable par tous les workers. Pendant une mise à jour progressive, un flow qui utilise `tools` attend donc un worker mis à jour au lieu d'échouer.

## Tests

- `Encoding.Plugins.Tests/ScriptToolsTests` : composition des programmes, exécution JavaScript et Python (outil autorisé, outil refusé, code de sortie d'un échec, génération et sondage d'un média synthétique), avec et sans Landlock.
- `Encoding.Runtime.Tests/ScriptToolsScenarios` : le schéma déclare `useTools`, la durée maximale est relevée, l'attribution exige `script-tools`.
- Non exécuté dans cette PR : les scénarios sous Landlock réel (le conteneur de développement refuse d'appliquer Landlock, comme pour `ProcessTests`) ; ils sont à lancer sur un hôte qui le permet.
