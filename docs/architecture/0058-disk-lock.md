# 0058 — Verrou de disque

Statut : implémentée (cinquième PR du plan « exécuter un flow FileFlows »). Plugin Base 1.5.0, migration 0035.

## Besoin

Deux encodages lourds sur le même disque physique (un NAS, un pool ZFS) se ralentissent mutuellement. Le verrou de média de 0015 est par fichier et ne l'empêche pas. FileFlows répond par un bloc de verrou ; ici le verrou est natif, durable et rattaché au bail de l'exécution.

## Décision

Deux composants Base, sans outil requis (tout worker peut les exécuter) :

- `base-disk-lock` (sorties `acquired`, `busy`, `error`) : prend le verrou du disque nommé dans le champ `disk`. L'identifiant est libre mais normalisé (`DiskLockKey` : minuscules, 64 caractères au plus, `[a-z0-9._ -]`). `busy` ne bloque ni n'attend : le flow le relie à « Reporter le traitement » (`base-defer-processing`) ou « Attendre » (`base-wait`). La variable `disk.<nom>` contient `acquired` et l'exécution qui détient le disque.
- `base-disk-unlock` (sorties `success`, `error`) : libère le verrou, sans effet si l'exécution ne le tient pas.

## Garanties

- Table `disk_locks(disk_key PRIMARY KEY, execution_id, lease_epoch, lease_until)`. La prise est une seule instruction `INSERT … ON CONFLICT DO UPDATE … WHERE` : elle réussit si le verrou est libre, déjà tenu par la même exécution (idempotent) ou expiré. L'exclusion est garantie dans une base SQLite locale ; les bases de plusieurs serveurs ne sont pas transactionnellement partagées.
- Prise et libération sont **fenced** par le bail et l'époque de l'exécution (`RuntimeRepository.FenceAsync`) : un worker périmé reçoit 409 et ne peut ni prendre ni libérer un verrou. Libérer un disque tenu par une autre exécution ne change rien.
- Le verrou suit le bail : `RenewAsync` le prolonge de 60 secondes avec l'exécution ; une exécution perdue le laisse expirer, et la prise suivante le reprend sans intervention.
- Libération automatique à la fin et à la récupération de l'exécution (`RuntimeRepository.ReleaseAsync`), à l'entrée en attente durable et au relais vers un autre worker. Le média reste réservé pendant l'attente ; seul le disque est rendu.
- Pas d'interblocage : `busy` n'attend jamais en tenant d'autres verrous, et un report termine l'exécution en libérant tous ses verrous.
- Métrique Prometheus `encoding_disk_locks_active`.

## Limites

- Le nom du disque est saisi dans le bloc. Un champ « disque » par accès bibliothèque (et sa propagation automatique) demanderait base de données, API et interface, et n'est pas inclus.
- Le verrou n'empêche pas un traitement hors de ce moteur d'utiliser le disque.

## Tests

`DiskLocksSerializeHeavyWorkPerDisk` (Runtime.Tests, SQLite) : normalisation, prise idempotente, occupé avec détenteur, course concurrente, époque et worker périmés, reprise après expiration, libération idempotente, prolongation par le renouvellement, routes worker, libération à l'annulation et à l'attente. Le catalogue et l'éligibilité sont vérifiés dans `SubtitleScenarios`.
