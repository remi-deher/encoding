# Test d'intégration à 3 nœuds (Docker)

Cluster réel de 3 instances indépendantes de l'image unique `encoding`, sur un réseau Docker isolé (`internal: true`), avec des
médias synthétiques et des identifiants temporaires. Aucun code métier n'est modifié ; seuls les API, scripts et contrats existants sont utilisés.

| Nœud | Rôles | Stockage propre | Accès bibliothèque `test-lib` |
|---|---|---|---|
| `node1` | serveur + worker (premier membre) | volume `node1-data` (identité, secret local) | direct `/media/lib` |
| `node2` | serveur + worker, même base PostgreSQL | volume `node2-data` | direct `/media/lib` |
| `node3` | worker seul (jumelé par clé, approuvé par l'administrateur) | volume `node3-data` | direct `/mnt/shared-media` (chemin différent) |
| `postgres` | PostgreSQL 17, base commune de node1 et node2 | volume `pg-data` | — |

Chaque nœud est limité à 1 CPU / 2 Gio (`cpus`, `mem_limit`) et à 1 tâche simultanée ; PostgreSQL à 0,5 CPU / 512 Mio.
Les volumes `flow-backups`, `runtime-artifacts` et `plugin-keys` sont partagés par les deux serveurs (exigence de 0015 : artefacts partagés entre serveurs).

## Reproduire

### Initialisation cloud recommandée

Depuis n'importe quel checkout du dépôt, lancez :

```bash
backend/tests/cluster-3-nodes/run-cluster.sh
```

Le script récupère `origin/main`, construit un snapshot propre à ce commit sans toucher au checkout courant, puis démarre le cluster avec un nom et des secrets dédiés à ce commit. Il attend que PostgreSQL et les deux API serveur soient prêtes avant l'enrôlement. Au premier lancement, l'image est construite ; les caches BuildKit npm et NuGet ainsi que l'image identifiée par le SHA de `main` sont réutilisés aux lancements suivants. Si `CODEX_PROXY_CERT` est fourni dans l'environnement cloud, le script le passe en secret BuildKit et conserve la validation TLS.

Le chemin du snapshot, le commit utilisé, un fichier `test-env.sh` à sourcer pour les commandes suivantes et les commandes Compose de nettoyage sont affichés à la fin. Ce fichier conserve l'accès direct au démon et ajoute les IP du cluster à `NO_PROXY`, nécessaire aux scripts d'API lancés depuis l'hôte cloud. En cas d'échec pendant l'initialisation, les journaux sont enregistrés sous `/tmp/encoding-cluster-main-<sha>/startup-failure.log` et le projet Compose temporaire est arrêté.

Après l'initialisation, sourcez le `test-env.sh` affiché et placez-vous dans le sous-dossier `backend/tests/cluster-3-nodes` du chemin `Sources`. Exécutez ensuite les étapes de flow et de scénarios ci-dessous. Le script `setup-flow-library.py` désactive maintenant la surveillance automatique avant l'attribution du flow afin que seule la découverte contrôlée mette les fichiers en file.

### Initialisation manuelle

```bash
cd backend/tests/cluster-3-nodes
export ENCODING_IMAGE=encoding:cluster-test MEDIA_DIR=/tmp/enc-test/media/lib SETUP_TOKEN=$(openssl rand -hex 8) PG_PASSWORD=$(openssl rand -hex 8)
export COMPOSE="docker compose -f $PWD/compose.yml"

# 1. Depuis la racine du dépôt ; en environnement cloud, privilégier run-cluster.sh ci-dessus.
docker build -f ../../../backend/Dockerfile -t "$ENCODING_IMAGE" ../../../

# 2. Médias synthétiques (2-5 s + un fichier corrompu), puis cluster
docker run --rm --network none --entrypoint /bin/bash -v "$PWD:/test:ro" -v "$MEDIA_DIR:$MEDIA_DIR" "$ENCODING_IMAGE" /test/gen-media.sh "$MEDIA_DIR"
$COMPOSE up -d --wait --wait-timeout 180
python3 bootstrap.py                  # administrateur, enrôlement de node1/node2, jumelage + approbation de node3 (codes de vérification comparés)
python3 check-cluster.py              # vue de chaque serveur, rôles, outils, inventaire, identités uniques (code 1 si incohérence)
python3 setup-flow-library.py         # flow H.264 (plan FFmpeg), bibliothèque test-lib, accès des 3 nœuds, attribution, découverte

# 3. Encodage distribué, concurrence, contrôle des sorties
./sampler.sh /tmp/t4.tsv 120 &  python3 enqueue.py            # tous les fichiers
python3 analyze-samples.py /tmp/t4.tsv                        # exclusivité par média, capacité par nœud, réservations
./verify-outputs.sh $MEDIA_DIR                                # sources (SHA-256), sorties (ffprobe, décodage complet)
python3 concurrent-enqueue.py node1 node2                     # test C : 8 requêtes simultanées sur 2 serveurs

# 4. Pannes (clips de 60 s nécessaires pour tuer un nœud en plein traitement)
docker run --rm --network none --entrypoint /bin/bash -v "$PWD:/test:ro" -v "$MEDIA_DIR:$MEDIA_DIR" "$ENCODING_IMAGE" /test/gen-long-media.sh "$MEDIA_DIR" && python3 discover.py
./test-a-worker-loss.sh results/ta.log     # A : SIGKILL de node3 pendant des traitements, puis docker start
./test-b-server-loss.sh results/tb.log     # B : SIGKILL de node1 (serveur+worker) ; node2 reste ; puis docker start node1
python3 test-d-rejoin.py                   # D : départ refusé pendant un bail, départ propre, révocation, nouveau jumelage
python3 test-transfer.py                   # accès Transfert (WebDAV) pour node3, « non pris en charge » pour node2
$COMPOSE down -v
```

`psql.sh "<SQL>"` interroge la base du cluster ; `agent.py <service> GET|POST <chemin>` appelle l'API locale (loopback:8081) d'un agent avec le secret local de l'image (0044).
Les journaux et échantillons de cette exécution sont dans `results/`.

## Résultats (exécution du 2026-10-15/16)

Légende : ✅ réussi (preuve dans `results/`) · ❌ défaut produit (bug) · ⚠️ limite / à améliorer · ➖ non testé.

| # | Vérification | Résultat |
|---|---|---|
| 1 | Démarrage des 3 nœuds, migrations (34, une fois) | ✅ |
| 2 | Visibilité dans le cluster : node1 et node2 voient les mêmes 3 nœuds (`check-cluster.py`) | ✅ |
| 3 | Communication serveurs ↔ workers (heartbeat 5 s, claim, renouvellement, finish) | ✅ |
| 4 | Rôles et capacités détectés (serveur+worker / worker), ffmpeg 6.1.1, ffprobe, mkvtoolnix 82, mediainfo 24.01, imagemagick, python ; encodeurs *utilisables* vérifiés : libx264, libx265, libsvtav1, libaom-av1 (GPU : « détecté » seulement) | ✅ |
| 5 | Inventaire CPU / mémoire / OS (1 CPU, quota cgroup respecté) | ✅ — mémoire : voir F3 |
| 6 | PostgreSQL : seuls node1 et node2 ouvrent des connexions (6 chacun), node3 aucune ; sessions partagées (connexion sur node1, utilisable sur node2) | ✅ |
| 7 | Cohérence : 3 identités, 3 jetons distincts, 0 quarantaine, 0 incompatible, 0 réservation résiduelle, 0 révision d'événement dupliquée | ✅ |
| 8 | Rattachement : node3 jumelé par clé + approbation, codes de vérification identiques des deux côtés | ✅ |
| 9 | Encodage distribué : 8 médias soumis ensemble → 3 vagues, un média par nœud et par vague, répartition équitable | ✅ |
| 10 | Un média jamais traité par deux workers (130 échantillons à 1 Hz : 0 cas), pic de 1 tâche `running` par nœud, 1 réservation par tâche | ✅ |
| 11 | Sorties : H.264 + AAC, durée à ±0,15 s de la source, décodage complet sans erreur (ffmpeg `-f null`), 54 sorties vérifiées au total, 0 KO | ✅ |
| 12 | Sources intactes (SHA-256 du manifeste, vérifiés après chaque scénario) ; sorties sous `.encoding-results/<id>/` sans écrasement | ✅ |
| 13 | Base : `result_sha` = SHA-256 du fichier publié, `published_path`, métadonnées avant/après, étapes et worker de chaque étape | ✅ (8/8) |
| 14 | Fichier corrompu | ❌ D1 |
| C | Concurrence : 8 requêtes simultanées (4 sur node1, 4 sur node2) pour les mêmes 8 fichiers → exactement 8 exécutions créées, 56 doublons refusés (`skipped`), 0 double traitement | ✅ |
| A | Perte brutale de node3 : détection hors ligne en ≈ 32 s (seuil 30 s), bail non renouvelé et expiré à +60 s, aucun doublon, réintégration avec la même identité sans quarantaine ; **reprise de la tâche en cours non réalisée tant que node3 est absent** | ✅ détection / ❌ reprise (D2) |
| B | Perte brutale de node1 (serveur+worker) : node2 continue d'authentifier, d'exposer l'API et d'attribuer (4 tâches terminées seul) ; retour de node1 : aucune incohérence (28 sorties OK) ; node3 devient inactif (L1) ; tâches détenues par node1 et node3 non reprises avant leur retour (D2) | ✅ continuité du serveur / ❌ reprise (D2) / ⚠️ L1 |
| D | Détachement/rattachement de node3 : départ refusé (409) pendant un bail ; départ propre (204) ; ancienne identité révoquée (`deleted_at`, accès supprimés, historique conservé, heartbeat refusé 401) ; nouvel identifiant ; aucune tâche tant que l'accès n'est pas reconfiguré, puis participation (2 tâches) | ✅ |
| T | Accès *Transfert* WebDAV (node3 sans montage, source node1) : tâches exécutées par node3, sorties publiées côté node1, node2 (*non pris en charge*) n'en reçoit aucune | ✅ |
| — | Reconstruction depuis zéro (`down -v` + `bootstrap.py`) : mêmes résultats avec de nouvelles identités | ✅ |

### Défauts et limites

**D1 — ❌ bug : un média illisible fait boucler l'exécution indéfiniment (sévérité haute).**
Avec `corrupt.mp4` (en-tête valide, `moov` absent), `ffprobe` échoue à l'étape d'entrée. Le worker journalise « Échec de l'outil pendant l'étape input », envoie un checkpoint `outputId="error"`, et le serveur répond 409 sur `POST …/steps/complete`. Le worker traite tout 409 comme un bail perdu (« reprise après expiration du bail »), attend 60 s, la tâche est reprise (époque 1, 2, 3, …) et recommence, sans jamais passer à `failed`. La réservation du média reste prise. Reproduit trois fois (dont sur cluster vierge). `POST /executions/{id}/cancel` débloque correctement (état `cancelled`, réservation libérée).
Cause probable (lecture du code, non instrumentée) : `RuntimeRepository.CompleteStepAsync` refuse un `outputId` que le composant ne déclare pas (`component.Outputs.Any(o => o.Id == cp.OutputId)`), or `base-input-video` ne déclare que `success` ; `RuntimeFlowRunner` traite un `MediaToolException` des composants `base` par `output = "error"` sans tenir compte de cette règle, puis range le 409 dans la catégorie « bail perdu ». Attendu (0015) : « une erreur non traitée échoue ».

**D2 — ❌ bug : une tâche détenue par un worker perdu n'est reprise que lorsque ce worker revient (sévérité haute).**
Après `kill -9` de node3 : bail expiré à 23:50:35, tâche toujours `running` sur node3 à 23:52:30 alors que node1 et node2 étaient libres et avaient le même accès direct ; dès le retour de node3 (docker start), **node1** la reprend à l'époque 2 et la termine. Même comportement dans le test B (tâche de node1 reprise par node2 seulement après le retour de node1). La cause est dans `RuntimeRepository.ClaimAsync` : en accès direct, `execution_work.source_node_id` est fixé au worker lui-même au premier claim ; à la reprise, la branche `item.Source is {} originalSource` exige que ce nœud d'origine soit encore en ligne avec un accès direct, sinon `continue`. Le mécanisme prévu pour un relais (`nodes-handoff`, 0021) bloque donc la reprise standard (0015, AGENTS.md : « récupérables après la perte d'un nœud »). Aucune perte de données ni double traitement, mais la reprise est indisponible pendant l'absence du nœud.

**L1 — ⚠️ limite : un worker n'a qu'un point d'entrée (`Cluster:ApiBaseUrl`).** Avec node1 arrêté, node3 (configuré sur `http://node1:8080/`) est hors ligne et inactif alors que le serveur node2 est disponible. La découverte DNS-SD n'existe pas sur un réseau bridge Docker. Pistes : liste d'URL, ou adresse portée par un répartiteur de charge.

**F3 — ⚠️ mineur : `inventory.memoryMb` n'est pas la mémoire du conteneur.** `HardwareInventory` lit `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` ; pour une limite de 2 Gio on obtient 1536 Mo (75 %, plafond du GC).

**Comportement documenté, à connaître pour les tests :** la surveillance des bibliothèques est active par défaut (`watch_enabled` vrai, scan 60 s) : une bibliothèque nouvellement attribuée met en file tous ses fichiers détectés sans action de l'utilisateur.

### Problèmes d'environnement (pas des défauts du produit)

- Le démon Docker n'était pas démarré (`dockerd` lancé à la main).
- Le proxy de sortie ré-signe le TLS : `dotnet restore` échoue (`UntrustedRoot`) dans le build ; contourné par des bases locales `…:10.0-ca` (CA ajouté) injectées via l'`ARG DOTNET_VERSION` existant, sans toucher au Dockerfile.
- Docker Hub : `429 Too Many Requests` sur `node` et `postgres` (nouvelles tentatives).
- Pas de GPU : l'agent journalise le repli CPU ; cgroup v1.
- Tous les nœuds partagent l'hôte Docker ; les volumes « partagés » entre serveurs ne prouvent donc pas un stockage multi-hôte.

### Non testé

GPU/encodage matériel ; plugins Sonarr/Radarr/Plex/Jellyfin et scripts sous Landlock (Landlock ABI 7 disponible sur l'hôte, non exercé) ; bloc `nodes-handoff` ; remplacement de source ; vidange (`draining`), quarantaine d'identité dupliquée (déclenchement non provoqué ; unicité des identités et jetons seulement constatée) ; mode SQLite ; arrêt propre (SIGTERM) d'un nœud ; perte du **seul processus serveur** d'un conteneur en laissant son worker vivant (le test B coupe tout le conteneur node1) ; HTTPS.

### Améliorations prioritaires

1. **D1** : un échec d'outil sur une étape sans sortie `error` doit conduire à `failed` (le serveur doit accepter ce checkpoint terminal, ou le worker appeler `finish` en échec) ; distinguer dans le worker un 409 « bail perdu » d'un checkpoint refusé.
2. **D2** : ne conditionner la reprise au nœud d'origine que pour un relais explicite (`handoff_selector` / mode transfert) ; en accès direct, laisser tout worker ayant un accès direct valide reprendre. Ajouter un scénario « perte du worker détenteur » à `Encoding.Runtime.Tests` (`Harness.Failover.cs`) : il n'a pas détecté ce cas.
3. **L1** : plusieurs adresses de serveur pour un worker.
4. **F3** : lire la mémoire du cgroup (ou l'hôte) plutôt que la valeur du GC.
