CREATE TABLE cluster_nodes (
    id uuid PRIMARY KEY,
    name varchar(128) NOT NULL,
    roles text[] NOT NULL,
    max_concurrent_tasks integer NOT NULL CHECK (max_concurrent_tasks BETWEEN 0 AND 1024),
    capabilities jsonb NOT NULL DEFAULT '[]'::jsonb,
    last_seen_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_cluster_nodes_last_seen_at ON cluster_nodes (last_seen_at);
