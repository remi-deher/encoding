CREATE TABLE execution_work (
 execution_id uuid PRIMARY KEY REFERENCES executions(id) ON DELETE RESTRICT,
 library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE RESTRICT,
 media_path text NOT NULL,
 media_key text NOT NULL,
 source_node_id uuid REFERENCES cluster_nodes(id),
 worker_node_id uuid REFERENCES cluster_nodes(id),
 lease_epoch bigint NOT NULL DEFAULT 0,
 lease_until timestamptz,
 available_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 source_sha varchar(64),
 result_sha varchar(64),
 result_extension varchar(16),
 publication_requested boolean NOT NULL DEFAULT false,
 published_path text,
 before_metadata jsonb,
 after_metadata jsonb
);
CREATE TABLE media_reservations (
 library_id uuid NOT NULL REFERENCES libraries(id),
 media_key text NOT NULL,
 execution_id uuid NOT NULL REFERENCES executions(id),
 lease_epoch bigint NOT NULL,
 lease_until timestamptz NOT NULL,
 PRIMARY KEY(library_id,media_key)
);
CREATE TABLE execution_steps (
 execution_id uuid NOT NULL REFERENCES executions(id),
 node_id text NOT NULL,
 invocation_id uuid NOT NULL UNIQUE,
 started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 completed_at timestamptz,
 completed_revision integer,
 idempotent boolean NOT NULL,
 checkpoint jsonb,
 PRIMARY KEY(execution_id,node_id)
);
CREATE TABLE execution_artifacts (
 execution_id uuid NOT NULL REFERENCES executions(id),
 sha256 varchar(64) NOT NULL,
 byte_length bigint NOT NULL CHECK(byte_length > 0),
 PRIMARY KEY(execution_id,sha256)
);
CREATE INDEX ix_execution_work_due ON execution_work(available_at,lease_until);
CREATE TABLE execution_inputs (
 execution_id uuid NOT NULL REFERENCES executions(id),
 input_path text NOT NULL,
 sha256 varchar(64) NOT NULL,
 PRIMARY KEY(execution_id,input_path),
 FOREIGN KEY(execution_id,sha256) REFERENCES execution_artifacts(execution_id,sha256)
);
ALTER TABLE executions DROP CONSTRAINT executions_state_check;
ALTER TABLE executions ADD CONSTRAINT executions_state_check CHECK(state IN ('queued','running','cancelling','succeeded','failed','cancelled','deferred'));
