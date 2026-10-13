# 0006 — Scan obligatoire et métadonnées avant/après

## Statut

Décision retenue. L’exécution et la persistance des instantanés restent à implémenter avec le moteur de flow et l’historique des tâches.

## Comportement attendu

Le moteur conserve les métadonnées de la source avant toute modification du média. Une entrée spécialisée analyse le média immédiatement. Une entrée Fichier générique fournit d’abord le contexte du fichier et permet des scripts préparatoires sans métadonnées de pistes. Le scan intervient à la première demande d’analyse, ou automatiquement avant la première opération qui exige ces informations ou transforme le média. L’analyse initiale reste obligatoire à ce stade ; un script ne peut pas contourner cette garantie par une écriture directe. Le résultat temporaire est également analysé avant validation et publication.

Le worker éligible effectue le scan sur un fichier local, après réception complète et vérification du transfert lorsqu’un transfert est requis. L’éligibilité doit inclure les capacités nécessaires au scan du type de média, en plus des outils requis par les actions. Pour vidéo et audio, FFprobe fournit le scan initial ; MediaInfo peut enrichir les informations techniques, notamment HDR. Pour les images et sous-titres, utiliser un analyseur adapté au format, sans imposer un outil incapable de lire ce format.

## Instantanés et historique

- **Avant** : scan de la source, conservé de façon immuable pour la tentative d’exécution.
- **Courant** : métadonnées accessibles aux conditions et variables, actualisées après une action qui change le média, ou à la demande explicite d’une action de lecture.
- **Après** : scan de la sortie temporaire candidate à la publication, associé au résultat de validation. Les résultats annexes (miniatures, pistes extraites, rapports) sont identifiés séparément ; ils ne remplacent pas arbitrairement le média courant.

Chaque instantané doit identifier le média et sa tentative d’exécution, la version du flow, la date d’analyse, l’analyseur et sa version, le conteneur, la taille, la durée et les pistes disponibles. Les caractéristiques vidéo/audio/sous-titres, langues, indicateurs, chapitres et pièces jointes sont conservées lorsque l’analyseur les expose. Les informations absentes restent absentes, elles ne deviennent pas des valeurs certaines ou des zéros.

Conserver les données normalisées pour l’interface et la sortie structurée brute de l’analyseur pour permettre diagnostic et évolutions du modèle. Une nouvelle tentative crée de nouveaux instantanés associés à cette tentative. Les comparaisons avant/après doivent porter sur les mêmes unités et distinguer changement de codec, de pistes, de durée, de résolution et de taille.

## Garanties

- Un échec du scan initial bloque le traitement et conserve sa cause dans l’historique.
- Un échec du scan de sortie empêche la publication et préserve la source.
- Un scan réussi décrit le média ; il ne prouve pas à lui seul un décodage complet ni l’intégrité du transfert. Ces validations restent distinctes.
- Une actualisation du scan courant ne modifie pas l’instantané **Avant**.
- Une tâche échouée conserve les scans réellement obtenus. Elle n’invente pas de résultat **Après**, ni de publication réussie.
- L’instantané de sortie validée est persisté avant la publication. L’historique distingue sortie candidate, validation et publication ; il ne marque la réussite qu’après publication confirmée.
- Les métadonnées sensibles et les chemins locaux ne sont pas exposés par défaut dans les journaux publics.

## Limites actuelles

Le dépôt ne contient pas encore de moteur de flow, de file de tâches durable ou d’historique d’exécution. Le catalogue UI est disponible ; cette décision définit les garanties à respecter lors de leur implémentation et ne constitue pas un scanner actif.
