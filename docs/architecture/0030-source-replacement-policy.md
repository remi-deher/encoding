# 0030 — Politique de remplacement du fichier source

## Décision

Le remplacement du fichier source par le résultat validé est une politique de **bibliothèque, désactivée par défaut** (`library_replacement_policies`). Sans politique active, le comportement de 0015 est inchangé : le résultat est publié sous `.encoding-results/<exécution>/` et la source n’est jamais touchée.

Politique : `enabled` et période de grâce de 1 à 8 760 heures (168 par défaut), avec révision pour refuser les écritures concurrentes. API : `GET/PUT /api/v1/libraries/{id}/replacement-policy`. Aucune interface Angular n’est livrée avec cette décision.

## Instantané et responsabilité

La politique est figée sur l’exécution quand le worker demande la publication (`execution_work.replacement_grace_hours`) : modifier la politique ensuite n’affecte pas une demande déjà émise. Le worker ne remplace jamais rien lui-même. Seul le nœud d’accès direct à la bibliothèque, qui détient déjà la réservation du média et contrôle le bail, effectue le remplacement après avoir publié et revérifié le résultat distinct.

## Protocole (nœud d’accès)

1. Contrôler que la source a toujours l’empreinte analysée.
2. Copier la source dans `.encoding-replaced/<exécution>/original.<ext>` (fichier temporaire, empreinte vérifiée, date de modification conservée, publication sans écrasement).
3. Copier le résultat validé dans un fichier temporaire voisin de la cible, vérifier son empreinte, puis revérifier bail, état de l’exécution et empreinte de la source.
4. Basculer : renommage atomique par-dessus la source si l’extension est identique ; sinon publication sans écrasement du nouveau nom puis suppression de l’ancien nom (la sauvegarde existe déjà). Un fichier tiers portant le nom cible n’est jamais écrasé.
5. Vérifier l’empreinte finale. En cas d’échec après le début de la bascule, la source est restaurée depuis la sauvegarde.

Chaque étape est reprenable : après perte du nœud, une sauvegarde sans bascule est complétée, et une bascule terminée mais non confirmée est reconnue (`ResumeAsync`) puis confirmée au serveur sans être refaite. Le rapport (`replaced` ou `failed`) accompagne la confirmation de publication. Un remplacement échoué ou annulé ne fait pas échouer l’exécution : le résultat distinct reste valide, la source est intacte et le détail le dit.

Le serveur n’accepte un rapport que si le chemin remplaçant est dans le dossier du média et si la sauvegarde est exactement `.encoding-replaced/<exécution>/original.<ext>`. Un agent qui ne connaît pas encore le rapport confirme sans remplacement (comportement sûr pendant une mise à jour progressive).

## Registre, période de grâce, restauration

`source_replacements` conserve par exécution l’état (`replaced`, `rollback_requested`, `rolled_back`, `finalized`, `retained`, `failed`), les chemins, les empreintes et l’échéance, écrits dans la même transaction que la réussite de l’exécution.

- **Restauration** : `POST /api/v1/executions/{id}/replacement/rollback`, uniquement pendant la période de grâce. Elle réserve le média (`media_reservations`, époque -1, 15 minutes) pour qu’aucun traitement ne le modifie en parallèle ; la demande est refusée (`409`) si le média est en cours de traitement. Le nœud d’accès restaure octet pour octet (date comprise) et refuse si le fichier remplaçant a été modifié depuis ; l’échec ramène à `replaced` avec la raison.
- **Purge** : après l’échéance, le nœud d’accès supprime la sauvegarde seulement si le fichier remplaçant existe encore. Sinon la sauvegarde est conservée (`retained`) et demande une action humaine ; elle n’est jamais retentée automatiquement.
- Tous les états sont dans PostgreSQL : n’importe quel serveur lit et modifie le même enregistrement, et des transitions conditionnelles garantissent qu’une seule restauration démarre même si deux serveurs la reçoivent.

## Surveillance

Le fichier produit (et l’original restauré) est enregistré dans `media_replacement_marks` avec taille et date ; la sélection automatique l’ignore tant que ces valeurs ne changent pas. Sans cela, la surveillance retraiterait en boucle le résultat d’un remplacement. Un fichier modifié ensuite redevient éligible. Les dossiers `.encoding*` restent exclus de la découverte.

## Limites

- La sauvegarde est une copie complète : l’espace disque de la bibliothèque doit pouvoir contenir la source pendant la période de grâce.
- Le contrôle du dernier instant (bail, état, empreinte) n’est pas atomique avec le renommage : un processus externe qui modifie la source dans cette fenêtre est détecté par la vérification d’empreinte avant la bascule, pas après.
- Une restauration ne protège pas contre un autre nœud qui écrirait dans le montage en dehors du système.
- Pas encore d’interface Angular ; les endpoints sont disponibles.

## Tests

`SourceReplacementTests` couvre les garanties fichier (sauvegarde, bascule, changement de conteneur, refus, reprise, restauration, purge). Les scénarios PostgreSQL couvrent la politique, la période de grâce, la restauration, la protection de la surveillance, la perte d’un worker (expiration du bail, reprise unique, époques, reprise depuis le checkpoint, nœud d’accès hors ligne) et la perte d’un serveur (verrous libérés à la coupure, aucun double bail entre deux serveurs, confirmation idempotente, restauration unique). Un scénario complet avec FFmpeg remplace puis restaure une vraie source H.265.
