# 0056 — Bloc « Noms de pistes »

Statut : implémentée (troisième PR du plan « exécuter un flow FileFlows »). Plugin Base 1.3.0.

## Besoin

Les pistes audio et sous-titres d'un MKV portent des noms hétérogènes (« VFF », « Japanese 5.1 », vides…). Le lecteur affiche ces noms : ils doivent être uniformes (`Langue (variante) / codec / disposition / drapeaux`), sans perdre l'information de variante portée par l'ancien nom (VFF, VFQ, VOSTFR…), et une seule piste audio doit être par défaut.

## Décision

Le composant `base-track-names` (action ; sorties `success`, `unchanged`, `error`) fonctionne en deux modes :

- **Instantané** : lit `mkvmerge -J` et mémorise les noms et drapeaux d'origine dans la variable `tracks.origin`. À placer avant les blocs qui reconstruisent le fichier (FFmpeg, audio par règles, sous-titres), qui peuvent perdre ces noms.
- **Appliquer** (défaut) : calcule les noms avec `TrackNaming.Plan` (`Encoding.Contracts/Flows/TrackNaming.cs`), copie le fichier de travail et applique `mkvpropedit --edit track:=<uid> --set name=… [--set flag-default=…]`. Le codec et la disposition viennent du scan courant (ffprobe) ; la langue, la variante et les drapeaux viennent du nom/drapeaux d'origine quand l'instantané existe, sinon du fichier courant.

Le modèle est modifiable (`audioTemplate`, `subtitleTemplate` avec `{language} {variant} {codec} {layout} {flags}`) ; les segments vides sont supprimés. `singleDefaultAudio` et `defaultAudioLanguage` choisissent l'unique piste audio par défaut (langue préférée hors commentaire, sinon celle déjà par défaut, sinon la première hors commentaire).

## Garanties

- Seule la **copie de travail** du worker est modifiée (jamais la source de la bibliothèque) ; la publication et le remplacement restent ceux de 0015 et 0030.
- Un fichier non MKV ou déjà conforme donne la sortie `unchanged` sans copie.
- Sélection de piste par UID (`track:=uid`) : indépendante de l'ordre. Le sélecteur `track:@n` désigne le numéro de piste, pas l'UID ; `base-edit-track-properties` utilisait `@` avec un UID et est corrigé dans le même PR.
- Les outils MKVToolNix sont lancés avec `LC_ALL=C.UTF-8` : en locale « C » (conteneur sans `LANG`), `mkvpropedit` tronque les noms accentués (« Français » devenait « Fran ») et `mkvmerge -J` produit un JSON invalide.

## Compatibilité

Base passe en 1.3.0 ; les composants antérieurs étendent leur liste de versions compatibles. Un worker qui n'annonce pas `plugin/base` 1.3.0 ni `mkvtoolnix` ne reçoit pas un flow qui utilise le bloc.

## Tests

`TrackNameScenarios` (nommage, variantes conservées, éligibilité) dans Runtime.Tests ; `TrackNamesMediaTests` (vrai MKV synthétique, source intacte, accents) dans Plugins.Tests.
