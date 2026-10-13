CREATE TABLE node_enrollment_requests (
    node_id uuid PRIMARY KEY,
    pairing_key_hash bytea NOT NULL UNIQUE,
    node_token_hash bytea NOT NULL,
    name varchar(128) NOT NULL,
    roles text[] NOT NULL,
    max_concurrent_tasks integer NOT NULL CHECK (max_concurrent_tasks BETWEEN 0 AND 1024),
    capabilities jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL
);

CREATE INDEX ix_node_enrollment_requests_expires_at ON node_enrollment_requests (expires_at);
