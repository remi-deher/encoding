# 0017 — Retrait des nœuds et détail des capacités

La suppression retire un nœud du catalogue, révoque son jeton et supprime ses accès directs. Les accès par transfert dont il était la source sont désactivés. Les fichiers et l’historique sont conservés. La ligne du nœud devient une identité révoquée via deleted_at, ce qui empêche un agent encore lancé de recréer automatiquement son inscription. Un nouvel enrôlement exige une nouvelle identité locale.

DELETE /api/v1/nodes/{id} prend un verrou sur le nœud et refuse HTTP 409 si celui-ci participe à une exécution ou découverte avec un bail valide, en tant que worker ou source. Un nœud inexistant ou déjà retiré retourne 404. Les heartbeats, l’authentification runtime, l’attribution et la sélection des accès excluent les identités retirées. Les références historiques restent valides.

GET /api/v1/nodes/{id}/activity fournit les baux de traitement actifs, les totaux réussis/échoués/annulés, les tâches alimentées comme source et la dernière activité. Ces compteurs proviennent de PostgreSQL et ne sont pas limités aux 200 dernières exécutions affichées ailleurs.

Le drawer présente des cartes de synthèse, l’identité, la connexion et l’activité, puis une matrice des outils/plugins : version, médias, accélérations annoncées, encodeurs détectés et encodeurs vérifiés utilisables. L’agent contrôle les encodeurs vidéo catalogués sur une mire synthétique et conserve le résultat dans ses capacités ; le cluster ne déduit pas l’accès à un GPU de la seule présence d’un fichier de périphérique. Les anciens agents sans état de vérification restent visibles comme non vérifiés. Aucune donnée CPU/RAM matérielle non déclarée n’est inventée.

Le tableau commun ajoute showStatusLabel, optionnel et actif par défaut ; les bibliothèques et leurs accès affichent les interrupteurs sans texte Activée/Désactivée. DetailFieldList ajoute la disposition cards, et DrawerActionButton un ton danger et un état disabled. Les usages existants gardent leur présentation par défaut.

Un heartbeat d’un identifiant retiré reçoit HTTP 410 (le jeton étant effacé au retrait, seul l’identifiant le reconnaît ; un identifiant inconnu reste 401). L’agent abandonne alors son identité et en prend une neuve : le nœud local de l’image se réenrôle d’office, un nœud distant redevient autonome. Un enrôlement refusé en 409 (identifiant retiré ou pris) remplace aussi l’identité au lieu de la réessayer indéfiniment.
