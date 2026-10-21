# Reverse proxy et exposition

L'interface et l'API sont servies à la **racine** d'un (sous-)domaine (pas de sous-chemin type `/encoding/`). Le temps réel passe par SSE (`text/event-stream`), sans WebSocket.

## Réglages de l'application

| Réglage | Rôle |
| --- | --- |
| `Auth__TrustForwardedHeaders=true` | Lit `X-Forwarded-For` / `X-Forwarded-Proto` : quotas par IP réelle, HSTS et cookie `Secure` selon le schéma réel. |
| `Auth__KnownProxies=192.168.1.10` | Adresses du proxy (séparées par des virgules). **À renseigner** : sans liste, tout appelant direct peut forger ces en-têtes (un avertissement est journalisé). |
| `Auth__KnownNetworks=172.18.0.0/16` | Idem, en notation CIDR (réseau Docker du proxy). |
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

## Exécution sans root (optionnel)

L'image s'exécute en root. L'accès au GPU n'impose pas le root : ajouter le GID du groupe de `renderD128` (`group_add`, voir `docker-compose.gpu.yml`). Non vérifié : droits d'écriture des volumes existants (créés par root) et sandbox Landlock sous un autre utilisateur. Ne pas changer l'utilisateur sur un déploiement existant sans avoir testé sur un volume de copie.
