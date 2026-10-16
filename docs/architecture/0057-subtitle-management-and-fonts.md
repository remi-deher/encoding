# 0057 — Blocs « Gestion des sous-titres » et « Polices »

Statut : implémentée (quatrième PR du plan « exécuter un flow FileFlows »). Plugin Base 1.4.0.

## Besoin

Uniformiser les sous-titres d'un MKV en une étape : SRT convertis en ASS au même style proportionnel, ASS simples mis à niveau, doublons retirés, pistes forcées déduites, pistes nommées, sous-titre français par défaut, et polices du style embarquées. Le point sensible est de ne jamais abîmer un ASS soigné (typesetting, karaoké) ni une piste bitmap (PGS, VobSub).

## Décision

### `base-subtitle-manage` (sorties `success`, `unchanged`, `error`)

La logique est en C# dans `Encoding.Contracts/Flows/SubtitleManagement.cs` (planificateur pur, testé sans fichier) ; `MediaActions.SubtitleManageAsync` l'applique.

1. Les pistes texte sont extraites : `mkvextract` pour SRT/ASS/SSA d'un MKV (copie sans décodage), FFmpeg pour les autres formats texte ; les SRT sont convertis en ASS par FFmpeg. Une piste dont l'extraction est vide n'est retirée que si elle est confirmée vide (ASS sans réplique, SRT sans texte visible) ; sinon le bloc suit `error`.
2. `SubtitlePlanner.Plan` décide : piste forcée (indicateur, titre, ou texte d'une piste courte de 10 % ou moins contenu à 90 % ou plus dans la plus complète de même langue), piste complète, doublons (texte et horaires à 0,1 s près inclus dans une autre piste de même langue et mêmes marqueurs, jamais pour un ASS personnalisé), sous-titre français par défaut quand l'audio de référence (unique piste par défaut, sinon unique piste audio) n'est pas français, titres `Langue / Format / Forcé|Malentendant|Signs & Songs|Complet|Piste N` (N suit la position dans le fichier de sortie, donc stable après un retrait).
3. **Profil de style** : `AssProfile` est un profil intégré en lecture seule « Crunchyroll » : quatre styles (Default, Italique, TiretsDefault, Sign) dont les tailles, contours et marges sont proportionnels à la hauteur affichée (référence 360 lignes, rapport d'affichage, anamorphose et rotation pris en compte). Un SRT est converti avec cet en-tête ; un ASS simple voit son en-tête et ses quatre styles normalisés, ses événements conservés. Un ASS avec styles supplémentaires ou balises de placement (`\pos`, `\move`, `\clip`, `\p`…) ou sans section de styles standard reste **intact**.
4. Un seul `mkvmerge` écrit le nouveau MKV : les pistes non modifiées sont recopiées de la source (aucun aller-retour par FFmpeg), les pistes converties viennent des fichiers ASS générés, les doublons sont omis, les indicateurs défaut/forcé et les noms sont posés explicitement, l'ordre des pistes est conservé. Les pièces jointes, chapitres et étiquettes de la source sont conservés.
5. Rien n'est produit (`unchanged`) quand tout est déjà conforme : un second passage sur la sortie ne change plus rien. Un fichier non MKV n'est pas traité.

Options : `styleProfile` (`crunchyroll` ou `none`), `convertSrt`, `upgradeAss`, `removeDuplicates`, `inferForced`, `setTitles` (à désactiver si « Noms de pistes », 0056, nomme les pistes ensuite), `frenchDefault`.

### `base-add-fonts` (sorties `success`, `unchanged`, `error`)

Ajoute en pièces jointes les polices manquantes d'un **jeu déclaré par le nœud** : le dossier racine est `ENCODING_FONT_DIR` (défaut `/app/common/Fonts`), un sous-dossier par jeu (`fontSet`, nom simple sans séparateur). La liste `fonts` donne des noms de fichiers ; vide, toutes les polices `.ttf`, `.otf`, `.ttc` du jeu. Jamais de chemin libre ni de remontée de dossier ; un jeu ou une police introuvable donne `error` sans toucher au fichier. Les noms déjà présents (insensible à la casse) ne sont pas rattachés une seconde fois. Les polices ne sont pas versionnées dans le dépôt : elles sont montées sur les nœuds.

## Écarts avec le plan initial

- Le plan prévoyait des profils de style versionnés (brouillon + versions immuables, modèle 0027) : ce lot livre le profil intégré en lecture seule ; la persistance, l'API et l'éditeur de profils sont reportés à un lot dédié pour ne pas mêler migration et logique de sous-titres.
- Le script d'origine ré-importait toutes les pistes texte quand une seule changeait ; ici seules les pistes réellement converties ou mises à niveau sont réécrites.
- Les raccourcis d'optimisation du script d'origine (contrôle rapide de conformité par en-tête, extraction audio dans le même passage) ne sont pas repris : l'audio relève de `base-audio-rules` (0055).

## Compatibilité

Base passe en 1.4.0 ; les composants antérieurs étendent leur liste de versions compatibles. Un worker qui n'annonce pas `plugin/base` 1.4.0, FFmpeg et MKVToolNix ne reçoit pas un flow qui utilise `base-subtitle-manage` ; `base-add-fonts` demande MKVToolNix.

## Tests

`SubtitleScenarios` (Runtime.Tests) : profil, canevas, conversion, mise à niveau, ASS personnalisé, doublons, forcé déduit, titres, défaut, pistes vides, catalogue, éligibilité. `SubtitlesMediaTests` (Plugins.Tests) : MKV synthétique avec SRT, doublon, ASS personnalisé ; source intacte, second passage sans changement, polices.
