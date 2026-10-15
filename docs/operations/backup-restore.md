# Sauvegarde et restauration

## Ce qui doit être sauvegardé

| Donnée | Emplacement (Compose) | Criticité |
| --- | --- | --- |
| Base PostgreSQL : flows, bibliothèques, nœuds, exécutions, configuration des plugins | volume `encoding-postgres-data` | **Indispensable** |
| Clés Data Protection des plugins | volume `encoding-plugin-keys` (`PluginSettings__KeyDirectory`) | **Indispensable.** Sans elles, les secrets de plugins stockés en base ne sont plus déchiffrables. À sauvegarder avec la base, dans un lieu distinct. |
| Sauvegardes JSON des versions de flow | volume `encoding-flow-backups` (`FlowBackups__Directory`) | Recommandé. Filet de sécurité indépendant de PostgreSQL. |
| Artefacts d’exécution | volume `encoding-runtime-artifacts` (`Runtime__ArtifactDirectory`) | Facultatif. Reconstituables en relançant les tâches ; ne pas sauvegarder les exécutions terminées. |
| Identité d’un worker | volume `encoding-node-data` de chaque agent | Facultatif. Perdue, il faut ré-enrôler le worker. |
| Fichiers publiés | bibliothèques média | Hors périmètre de l’application : sauvegarde du stockage média habituel. |

Les sources ne sont jamais remplacées automatiquement, mais une sauvegarde du stockage média reste nécessaire avant toute politique de remplacement.

## Sauvegarde

PostgreSQL, à chaud et cohérente :

```bash
docker compose exec -T postgres pg_dump -U encoding -d encoding -Fc > encoding-$(date -u +%Y%m%dT%H%M%SZ).dump
```

Volumes de fichiers (les clés de plugins sont petites, à copier à chaque sauvegarde de base) :

```bash
for volume in encoding-plugin-keys encoding-flow-backups; do
  docker run --rm -v "backend_${volume}:/data:ro" -v "$PWD:/out" alpine \
    tar czf "/out/${volume}-$(date -u +%Y%m%dT%H%M%SZ).tgz" -C /data .
done
```

Le préfixe du volume (`backend_` ci-dessus) est le nom du projet Compose ; vérifier avec `docker volume ls`.

Conserver au moins une copie hors de l’hôte, chiffrée : les clés de plugins donnent accès aux secrets qu’elles protègent. Une rotation quotidienne de la base et une copie des clés à chaque changement sont un minimum raisonnable.

## Vérifier une sauvegarde

Une sauvegarde non restaurée n’est pas une sauvegarde. Régulièrement, restaurer dans une base jetable :

```bash
docker run --rm -d --name encoding-restore-check -e POSTGRES_PASSWORD=check postgres:17
docker exec -i encoding-restore-check pg_restore -U postgres -d postgres --no-owner < encoding-XXXX.dump
docker exec encoding-restore-check psql -U postgres -c "SELECT count(*) FROM flows" -c "SELECT max(version) FROM schema_migrations"
docker rm -f encoding-restore-check
```

## Restauration complète

1. Arrêter tous les serveurs et agents (`docker compose stop encoding`). Les workers ne doivent pas tenir de bail pendant la restauration.
2. Restaurer les clés de plugins dans `encoding-plugin-keys` **avant** de redémarrer un serveur.
3. Recréer la base vide puis restaurer :
   ```bash
   docker compose exec -T postgres psql -U encoding -d postgres -c "DROP DATABASE encoding" -c "CREATE DATABASE encoding OWNER encoding"
   docker compose exec -T postgres pg_restore -U encoding -d encoding --no-owner < encoding-XXXX.dump
   ```
4. Restaurer `encoding-flow-backups` si le volume est perdu.
5. Démarrer un serveur. Il applique les migrations manquantes sous verrou PostgreSQL et refuse de démarrer si le contenu d’une migration déjà appliquée a changé. Contrôler `GET /health/details` : `database`, `migrations` et `artifact-storage` doivent être `healthy`.
6. Démarrer les autres serveurs puis les agents. Les exécutions qui tenaient un bail au moment de la sauvegarde sont reprises à l’expiration du bail ; une invocation externe interrompue avant checkpoint exige une relance explicite (voir 0015). Vérifier la page Tâches.

Les médias ne sont pas modifiés par la restauration : les résultats sont publiés à côté de la source (`.encoding-results/`). Un artefact d’exécution absent après restauration oblige à relancer la tâche.

## Perte de PostgreSQL seul

Si la base est perdue sans sauvegarde mais que `encoding-flow-backups` existe, les versions de flows se récupèrent : chaque version publiée est `<flowId>.v<version>.json` et chaque brouillon `<flowId>.draft.r<revision>.json`. Réimporter le contenu des fichiers de version par `POST /api/v1/flows/import` (JSON ou archive ZIP). Bibliothèques, accès par nœud, configuration des plugins et historique ne sont pas reconstructibles par ce moyen.

## Perte d’un seul worker

Retirer le nœud depuis l’interface (l’identité retirée n’est jamais réactivée implicitement), puis enrôler une nouvelle installation et redéclarer ses accès aux bibliothèques.

## Mise à jour et retour arrière

Avant une mise à jour qui apporte des migrations, faire une sauvegarde de base. Les migrations ne sont pas réversibles : le retour arrière se fait en restaurant cette sauvegarde. Pendant une mise à jour progressive, un serveur plus ancien voit des migrations qu’il ne connaît pas et se déclare `degraded`, sans cesser de servir.
