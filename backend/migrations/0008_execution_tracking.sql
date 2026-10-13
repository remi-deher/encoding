CREATE TABLE executions (
 id uuid PRIMARY KEY,
 media text NOT NULL,
 library_name varchar(128) NOT NULL,
 flow_name varchar(128) NOT NULL,
 flow_version integer NOT NULL CHECK(flow_version > 0),
 flow_definition jsonb NOT NULL,
 state varchar(24) NOT NULL CHECK(state IN ('queued','running','cancelling','succeeded','failed','cancelled')),
 node_id uuid REFERENCES cluster_nodes(id) ON DELETE RESTRICT,
 progress numeric CHECK(progress BETWEEN 0 AND 100),
 detail text NOT NULL DEFAULT '',
 attempt integer NOT NULL CHECK(attempt > 0),
 retry_of uuid UNIQUE REFERENCES executions(id),
 revision integer NOT NULL DEFAULT 0,
 queued_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 started_at timestamptz,
 finished_at timestamptz,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX ix_executions_updated ON executions(updated_at DESC, id);
CREATE TABLE execution_events (
 id uuid PRIMARY KEY,
 execution_id uuid NOT NULL REFERENCES executions(id) ON DELETE RESTRICT,
 revision integer NOT NULL,
 state varchar(24) NOT NULL,
 detail text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 UNIQUE(execution_id,revision)
);
