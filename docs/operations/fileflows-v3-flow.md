# Flow « HEVC + AAC/AC3 + ASS (Crunchyroll) » : équivalent du flow FileFlows V3

Ce flow reconstruit, avec les blocs livrés par les PR #62 à #67, le flow FileFlows « V3 – HEVC + AAC/AC3 + ASS Crunchyroll ». Il est fourni comme fichier importable : [`flow-templates/hevc-aac-ass.flow.json`](flow-templates/hevc-aac-ass.flow.json). Un test (`CrunchyrollFlowIsValidImportableAndEndsExplicitlyOnEveryFailure`) vérifie que le fichier est exactement le graphe testé, qu'il passe la validation du catalogue et que chaque sortie de chaque bloc mène à une fin explicite.

## Mettre le flow en service

1. Page **Flows** : importer `hevc-aac-ass.flow.json` (import JSON, ADR 0014). Le flow est créé en version 1, identifiant fixe ; un second import identique est sans effet.
2. Ouvrir le flow dans l'éditeur : régler le nom du disque du bloc **Verrou de disque** (`disque-1` par défaut ; le même nom pour toutes les bibliothèques d'un même disque physique), le profil de qualité vidéo (par défaut : encodeur automatique, HEVC) et le jeu de polices. Publier.
3. Attribuer la version publiée à une bibliothèque. Si besoin, créer un modèle depuis cette version (ADR 0025) pour en dériver d'autres flows.
4. Prérequis sur les nœuds : FFmpeg, MKVToolNix, plugin `base` 1.5.0 ou plus, plugin `plex` 1.1.0 (déclarés dans `Runtime:Plugins`, voir `docker-compose.yml`), et le dossier de polices du nœud (`ENCODING_FONT_DIR/Crunchyroll/…`, ADR 0057). Un nœud qui ne satisfait pas ces conditions ne reçoit pas le flow.
5. Plex : renseigner l'adresse, le jeton et la correspondance de chemins du plugin (ADR 0032). Pour protéger le remplacement de la source, activer la politique de remplacement de la bibliothèque (ADR 0030, désactivée par défaut) ; la garde de Plex s'applique alors (ADR 0059).

## Déroulement

```
Fichier vidéo → Lecture Plex ──lecture──→ Reporter 10 min
                    │ libre / erreur Plex
                    ▼
              Verrou de disque ──occupé──→ Reporter 2 min
                    │ pris
                    ▼
        Noms de pistes (instantané) → Plan FFmpeg → Vidéo HEVC → Audio par règles
                    → Exécuter le plan (une écriture) → Sous-titres ASS → Polices
                    → Noms de pistes → Terminé

N'importe quelle étape en erreur → Échec (la source reste intacte)
```

## Correspondance FileFlows → encoding

| Bloc FileFlows | Bloc encoding | PR / ADR | Écart |
| --- | --- | --- | --- |
| Entrée VideoFile | `base-input-video` | — | Scan imposé avant toute transformation (0006) |
| 0. Pause lecture Plex | `plex-check-playback` (fichier + épisode précédent) → « Reporter » | 0059 | Report de 10 minutes sans occuper de worker ; pas de Watchdeck (disques en pause) |
| 3. Verrou par disque, report si occupé | `base-disk-lock` → « Reporter » (2 min) | 0058 | Nom de disque saisi dans le bloc |
| 0b. Relevé des pistes d'origine | `base-track-names` (instantané) | 0056 | Conserve les noms d'origine (VFF, VFQ…) |
| 3. Vidéo | `base-transcode-video` (HEVC, encodeur automatique) | 0027 | Pas de détection « déjà HEVC / HDR protégé » dans ce flow : à ajouter avec `conditions-match` si besoin |
| 4–5, 8–11. HEVC QSV et secours CPU | encodeur automatique du profil (QSV si utilisable, sinon CPU) | 0027, 0051 | Pas de bloc de secours sur échec d'exécution ; décodage matériel non exposé |
| 2. Audio : stéréo→AAC, 2.1+→AC3 | `base-audio-rules` (ajouté au plan) | 0055 | Disposition ambiguë : sortie « Erreur » |
| 1. Sous-titres | `base-subtitle-manage` | 0057 | Profil Crunchyroll intégré, non éditable |
| Polices | `base-add-fonts` | 0057 | Jeu déclaré par le nœud |
| 4. Assemblage mkvmerge en une écriture | `base-execute-processing` + les blocs ci-dessus | 0015, 0055–0057 | Contrôles de sortie génériques (durée, pistes, décodage), pas les 15 contrôles du script |
| 4b. Noms de pistes | `base-track-names` (appliquer) | 0056 | Modèle de nom modifiable |
| 29, 13. Garde Plex puis remplacement atomique | politique de remplacement de la bibliothèque + garde Plex | 0030, 0059 | Remplacement désactivé par défaut ; garde pour tous les remplacements du cluster |
| 14–15. Mise à jour et analyse Plex | `plex-refresh-library`, `plex-analyze-media` | 0032, 0059 | **Non inclus dans ce flow** (voir ci-dessous) |
| 26–27. Radarr / Sonarr | `radarr-refresh-movie`, `sonarr-refresh-series` | 0032 | **Non inclus** (voir ci-dessous) |
| Scripts avec `Flow.Execute` | `base-script` avec l'aide `tools` | 0054 | Bac à sable Landlock conservé |

## Comportement dans les cas d'échec

| Situation | Résultat |
| --- | --- |
| Lecture en cours dans Plex (fichier ou épisode précédent) | Exécution reportée de 10 min, aucun worker occupé |
| Disque occupé par un autre traitement | Reportée de 2 min ; le verrou de l'autre exécution est intact |
| Plex injoignable pendant la vérification | Le flow continue ; la garde de publication refuse le remplacement de la source (le résultat distinct est publié) |
| Disposition audio ambiguë | Sortie « Erreur » → Échec, source intacte |
| Outil ou plugin absent sur le nœud | Le nœud ne reçoit pas le flow |
| Perte du worker en cours de traitement | Le bail expire, un autre worker reprend au dernier point de contrôle sous une époque plus haute ; le disque reste réservé à l'exécution et l'ancien worker est écarté (test `DiskStaysReservedWhenItsWorkerIsLost`) |
| Perte du nœud d'accès pendant la publication | La publication est reprise sans double bascule (0030) |

## Limites connues

- **Mise à jour de Plex après remplacement** : la publication et le remplacement de la source ont lieu après la fin du flow ; un bloc placé dans le flow s'exécute donc avant. Ce flow n'appelle pas `plex-refresh-library` ni `plex-analyze-media` ; il n'existe pas encore de déclencheur « après publication ». Plex détecte le fichier remplacé par son propre scan, ou un second flow peut lancer ces blocs.
- **Radarr / Sonarr** : mêmes raisons ; non inclus.
- **QSV n'a jamais été vérifié sur un vrai GPU** (hôtes TrueNAS) : à tester avant de compter sur l'encodage matériel.
- **Modification sur place** (mkvpropedit avec sauvegarde de l'en-tête et restauration) n'existe pas : le flow travaille sur une copie.
- Non exercés dans le conteneur de développement : Landlock, `ProcessTests`, un vrai serveur Plex, les hôtes TrueNAS.
