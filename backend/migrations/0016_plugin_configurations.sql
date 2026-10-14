CREATE TABLE plugin_configurations (
 plugin_id text PRIMARY KEY,
 revision integer NOT NULL DEFAULT 1 CHECK(revision > 0),
 encrypted_values bytea NOT NULL,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE flow_plugin_configurations (
 flow_id uuid NOT NULL REFERENCES flows(id) ON DELETE CASCADE,
 plugin_id text NOT NULL,
 revision integer NOT NULL DEFAULT 1 CHECK(revision > 0),
 encrypted_values bytea NOT NULL,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(flow_id, plugin_id)
);
