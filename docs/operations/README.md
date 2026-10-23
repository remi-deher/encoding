# Exploitation : index

Documents opérationnels (procédures, supervision, suivis). Pour l'état du système et les invariants, voir plutôt [docs/architecture](../architecture/README.md).

| Document | Contenu | Fiche liée | État |
| --- | --- | --- | --- |
| [release.md](release.md) | Publication des images Docker, versions, installation de l'image unique | cluster | à jour |
| [backup-restore.md](backup-restore.md) | Sauvegarde et restauration SQLite, volumes, perte d'un nœud | replication-storage | à jour |
| [reverse-proxy.md](reverse-proxy.md) | Reverse proxy, en-têtes de sécurité, proxies de confiance, ports | auth-ops | à jour ; non testé derrière un vrai proxy |
| [monitoring.md](monitoring.md) | Santé, métriques Prometheus, alertes, journaux, rétention | auth-ops | à jour |
| [ai-providers.md](ai-providers.md) | Installation et connexion des fournisseurs IA | ai | à jour |
| [ai-metadata-enrichment.md](ai-metadata-enrichment.md) | Bloc d'enrichissement IA des métadonnées | ai, plugins | à jour |
| [fileflows-v3-flow.md](fileflows-v3-flow.md) | Reconstruire dans l'application le flow FileFlows V3 | plugins, flows | à jour ; non vérifié sur Plex/GPU/Landlock réels |
| [peer-cluster-preview.md](peer-cluster-preview.md), [peer-cluster-production-todo.md](peer-cluster-production-todo.md), [peer-cluster-alerts.yml](peer-cluster-alerts.yml) | Mode Raft optionnel | replication-storage | expérimental, avenir à décider |
| [todo-hardware-accelerated-tools-policy.md](todo-hardware-accelerated-tools-policy.md) | Règle d'or accélération matérielle (non codée) | plugins | à faire |
| [flow-blocks-agent-handoff.md](flow-blocks-agent-handoff.md) | Note de reprise de la stabilisation des blocs | ui, plugins | historique |
