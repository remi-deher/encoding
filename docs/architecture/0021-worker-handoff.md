# 0021 — Relais explicite entre workers

## Décision

Le plugin intégré Nœuds ajoute `nodes-handoff` : un point de relais dans un flow séquentiel. L'exécution, sa tentative, sa version publiée et la réservation du média restent identiques. Le prochain bloc s'exécute sous un nouveau bail sur un autre worker. Si ce worker perd ensuite son bail, cette tentative échoue ; elle ne reprend pas automatiquement au checkpoint.

Le sélecteur choisit un nœud précis ou un worker éligible. Sa configuration résulte des valeurs globales du plugin, des surcharges du flow puis de celles du bloc. Les configurations effectives des plugins sont figées à la première attribution et conservées chiffrées dans PostgreSQL pour les reprises et les relais. Une cible absente, retirée, occupée ou incompatible laisse le relais en attente ; aucun repli sur le worker précédent n'est autorisé.

## Transition atomique

La fin du bloc et la demande de relais sont enregistrées dans une seule transaction : checkpoint, événement, révocation du bail et changement d'époque. La réservation reste durable pendant l'attente du destinataire. Une attribution du relais rétablit un bail de 60 secondes et une réservation avec la nouvelle époque. Toute requête de l'ancien worker est refusée. Une répétition du checkpoint identique est reconnue sans effectuer de nouveau relais. L'expiration d'un bail après attribution au destinataire clôt la tentative en échec et libère la réservation.

L'état public reste `running` avec un détail « relais en attente » ; `execution_work.handoff_pending` distingue cette phase sans introduire un nouvel état public incompatible. L'annulation en attente libère la réservation via le mécanisme de récupération existant.

## Reprise et fichiers

Le checkpoint conserve variables, scans avant/après, plan et prochaine étape. Les fichiers sont identifiés par SHA-256, jamais par un chemin temporaire local. Le backend vérifie que le média courant et les artefacts annexes référencés sont durables avant le relais. Avant transformation, le destinataire récupère la source originale ; après transformation, il récupère l'artefact courant. Les téléchargements existants vérifient les empreintes et reprennent les copies partielles.

Le nœud d'accès à la bibliothèque reste fixé à la première attribution, y compris pour les fichiers annexes et la publication finale. L'accès du nouveau worker et les capacités requises jusqu'au prochain relais sont vérifiés par les mécanismes communs. Les contraintes historiques `nodes-run-on-node` et `nodes-require-capability` continuent de s'appliquer au segment courant.

La matérialisation locale est différée jusqu'à une étape qui nécessite un fichier : une entrée Fichier générique suivie d'un relais ne télécharge pas la source sur le premier worker. Une entrée spécialisée conserve son scan obligatoire.

## Transport et compatibilité

La première version utilise le stockage durable partagé et les endpoints de transfert existants. Le transfert direct entre workers pourra être ajouté avec négociation authentifiée, reprise, contrôle d'intégrité et repli vers ce stockage. Aucun serveur permanent ni connexion directe entre workers n'est nécessaire.

Le plugin Nœuds passe en version 1.2.0 avec le bloc de relance bornée. Les références 1.0.0 et 1.1.0 des composants précédents restent résolubles. Le nouveau bloc n'est attribué qu'aux agents annonçant 1.2.0 ; les anciens agents ne connaissent pas sa règle d'exécution.

## Garanties à vérifier

Relais avant et après transformation, contexte inchangé, reprises à chaque panne, doublons, refus des anciennes époques, conservation de la source et de la réservation, attente sans cible, annulation et sélection indépendante des capacités de chaque segment.

## Implémentation et vérification

La migration additive `0017_worker_handoff` ajoute l'état interne de relais, le sélecteur, le worker précédent, les configurations chiffrées et l'identité de fin de checkpoint. Une fin de checkpoint identique est acquittée même après la révocation du bail ; une variante du contenu ou de l'identité est refusée.

La suite `backend/tests/Encoding.Runtime.Tests` vérifie les dépôts sur PostgreSQL isolé, les endpoints et le runner réel. Elle couvre les fichiers originaux et transformés, les artefacts annexes, les doublons, les anciennes époques, les réclamations concurrentes, les configurations figées et les surcharges, les limites, les cibles hors ligne, l'accès bibliothèque, le retrait de la source et l'annulation. Deux parcours FFmpeg sur une vidéo synthétique vérifient la conversion en H.265 avant relais et la transmission d'un plan avant exécution, jusqu'à la publication réelle par le service source. La CI exécute ces mêmes scénarios.
