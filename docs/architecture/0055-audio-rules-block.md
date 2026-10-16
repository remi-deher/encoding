# 0055 — Bloc « Audio par règles »

Statut : implémentée (deuxième PR du plan « exécuter un flow FileFlows »). Plugin Base 1.2.0.

## Besoin

Uniformiser toutes les pistes audio d'un fichier en une seule étape, sans répéter un bloc d'encodage par piste ni écrire un script : stéréo en AAC, 2.1 et plus en AC-3 limité à 6 canaux, mono conservé. Le point sensible est la disposition des canaux : un 5.1 ne doit pas perdre son LFE et une disposition absente des métadonnées ne doit pas être devinée à la légère.

## Décision

Le composant `base-audio-rules` (action ; sorties `success`, `unchanged`, `error`) lit l'instantané courant du scan, décide piste par piste et **ajoute des étapes `base-transcode-audio` au plan FFmpeg commun** (mode « Ajouter au plan », par défaut) ou les exécute immédiatement (« Opération autonome »). Il n'a pas de second chemin d'exécution : l'écriture reste groupée avec les autres opérations FFmpeg du plan et suit les mêmes validations de sortie (0015).

### Règles (`AudioRulePlanner`, `Encoding.Contracts/Flows/AudioRules.cs`)

| Source | Cible |
| --- | --- |
| Stéréo, AAC | conservée |
| Stéréo, AC-3 | conservée (option `keepStereoAc3`, vraie par défaut), sinon AAC |
| Stéréo, autre codec | AAC 256 kb/s |
| Mono, AAC | conservée |
| Mono, autre codec | AC-3 mono 128 kb/s |
| 3 canaux et plus, AC-3 ≤ 6 canaux | conservée |
| 3 canaux et plus, autre | AC-3 limité à 6 canaux : 3 → 384, 4 → 448, 5 et 6 → 640 kb/s |
| Plus de 6 canaux | 5.1 si la disposition a un LFE, sinon 5.0 |

La disposition cible est imposée à la sortie (`-channel_layout`) avec le nombre de canaux (`-ac`) et la fréquence d'échantillonnage (`sampleRate`, 48 000 Hz par défaut, 0 pour conserver) ; la variante `(side)` d'un 5.1 est conservée. Une disposition qu'AC-3 ne sait pas porter est **refusée** avant tout remplacement.

### Disposition inconnue

Une piste dont `channel_layout` est absent, `unknown` ou seulement « N channels » ne peut être convertie que si sa disposition est établie, dans cet ordre :

1. E-AC-3 à 2 canaux : stéréo.
2. Titre contenant « stéréo », « stereo » ou « 2.0 » sur 2 canaux, sans piste déclarée de 2 canaux qui le contredise.
3. Toutes les autres pistes **déclarées** (jamais une piste déjà déduite) ayant le même nombre de canaux donnent la même disposition (`5.1` et `5.1(side)` sont équivalentes).

Sinon, le bloc suit sa sortie `error` : références contradictoires ou absentes. La disposition déduite est déclarée à FFmpeg en entrée (`inputLayout`) et journalisée. L'option `unknownLayout` = `fail` désactive toute déduction.

### Contrat et compatibilité

- `base-transcode-audio` accepte trois champs de plan facultatifs : `inputLayout`, `outputLayout`, `sampleRate`. Ils ne sont pas exposés dans son formulaire et ne changent rien aux flows existants.
- Plugin Base **1.2.0**. Les composants des versions 1.0.0 et 1.1.0 restent résolubles (`compatiblePluginVersions` étendu à `1.1.0`, `base-wait` compatible `1.1.0`). Un worker annonce `plugin/base` 1.2.0 ; un flow qui référence Base 1.2.0 n'est attribué qu'à un tel worker. Pendant une mise à jour progressive, les flows qui référencent Base 1.2.0 attendent un worker mis à jour.
- Éligibilité : le worker doit avoir les encodeurs FFmpeg `aac` et `ac3`.

## Limites

- Les règles sont une table fixe avec trois options, pas encore un tableau de règles libre : un tableau éditable demande un type de champ déclaratif nouveau (0011, 0019) et sera traité séparément si le besoin se confirme.
- Le titre d'une piste convertie n'est pas mis à jour (il peut citer l'ancien codec) : le bloc « Noms de pistes » du lot suivant s'en charge.
- Les durées de piste ne sont pas comparées après conversion en plus de la validation commune de sortie (durée du fichier, décodage complet).

## Tests

- `Encoding.Runtime.Tests/AudioRuleScenarios` : table de décision (stéréo, mono, 5.1 side, 7.1, 7.0, DTS, Opus), déduction de disposition, refus (références contradictoires ou absentes, titre contradictoire, disposition non AC-3, canaux inconnus), configuration des étapes de plan, déclaration du composant, compatibilité 1.1.0, éligibilité (encodeurs, version de Base).
- `Encoding.Plugins.Tests/AudioRulesMediaTests` : fichier synthétique réel (7.1 FLAC, stéréo AAC, 5.1 E-AC-3) converti par FFmpeg : 5.1 AC-3 avec LFE, piste conforme copiée, vidéo intacte, source inchangée, second passage sans changement.
