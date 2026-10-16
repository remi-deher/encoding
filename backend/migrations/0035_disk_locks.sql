-- Architecture 0058 : exclusion mutuelle des opérations lourdes sur un même disque, avec bail renouvelé comme celui de l'exécution.
CREATE TABLE disk_locks (
    disk_key varchar(64) PRIMARY KEY CHECK (disk_key ~ '^[a-z0-9][a-z0-9._ -]{0,63}$'),
    execution_id uuid NOT NULL REFERENCES executions(id),
    lease_epoch bigint NOT NULL,
    lease_until timestamptz NOT NULL,
    acquired_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX ix_disk_locks_execution ON disk_locks (execution_id);
