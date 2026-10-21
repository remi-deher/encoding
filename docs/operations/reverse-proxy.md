# Reverse proxy et exposition

L'interface et l'API sont servies à la **racine** d'un (sous-)domaine (pas de sous-chemin type `/encoding/`). Le temps réel passe par SSE (`text/event-stream`), sans WebSocket.

## Réglages de l'application

| Réglage | Rôle |
| --- | --- |
| **Paramètres > Réseau** | Proxies de confiance de **ce nœud** : adresses ou réseaux CIDR, avec un bouton « Détecter le proxy de cette connexion » (ouvrir l'interface à travers le proxy, puis cliquer). Enregistré dans `network-settings.json` du dossier de données, appliqué sans redémarrage. |
| `Auth__TrustForwardedHeaders=true` + `Auth__KnownProxies` / `Auth__KnownNetworks` | Valeurs de départ, lues seulement si aucun réglage n'a encore été enregistré dans l'interface. |
| `Auth__CookieSecure=true` | Force `Secure` sur le cookie de session derrière un proxy TLS. |
| `Security__Hsts` (vrai), `Security__HstsMaxAgeDays` (365) | HSTS, émis seulement quand la requête est HTTPS. |
| `Security__ContentSecurityPolicy` | Vide par défaut. Une CSP non essayée sur l'interface réelle peut la casser : à renseigner après essai. |
| `AllowedHosts=encoding.exemple.fr` | Restreint les noms d'hôte acceptés (défaut `*`). |
| `ASPNETCORE_ENVIRONMENT=Production` | Le compose du dépôt vaut `Development` par défaut ; surcharger en production. |

L'application pose elle-même `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy` et `Cache-Control: no-store` sur `/api/`.

## Ports

Publier l'UI (8090 sur TrueNAS) uniquement vers le proxy (pare-feu ou liaison à son adresse). Le port pair à pair 8443 ne doit être ouvert qu'entre les serveurs du cluster.

## Exemple Caddy

```
encoding.exemple.fr {
    encode zstd gzip
    reverse_proxy 192.168.1.50:8090 {
        flush_interval -1
        transport http { read_timeout 1h }
    }
}
```

## Exemple nginx

```
location / {
    proxy_pass http://192.168.1.50:8090;
    proxy_http_version 1.1;
    proxy_buffering off;
    proxy_read_timeout 3600s;
    client_max_body_size 0;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

## Exécution sans root

L'image s'exécute avec l'utilisateur `app` (UID 1654) et non plus en root. Deux conséquences :

- **GPU** : ajouter le GID du groupe de `/dev/dri/renderD128` au conteneur (`group_add`, déjà dans `docker-compose.gpu.yml` avec `GPU_RENDER_GID`, 107 sur TrueNAS SCALE). Sur une custom app TrueNAS, ajouter ce groupe dans la configuration du conteneur.
- **Volumes existants** : ceux créés par une ancienne image appartiennent à root. Avant la mise à jour, faire un snapshot ZFS (ou une copie), puis rendre les volumes à l'utilisateur, par exemple : `docker run --rm -v <volume>:/data alpine chown -R 1654:1654 /data` pour chaque volume de données. Les dossiers média montés doivent être lisibles (et écrits pour la publication) par l'UID 1654 ou son groupe.

Non vérifié : sandbox Landlock, accès GPU et écriture des volumes sous cet utilisateur sur de vrais TrueNAS ; à tester avant la production.
