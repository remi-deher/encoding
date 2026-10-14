ALTER TABLE execution_work
 ADD COLUMN handoff_pending boolean NOT NULL DEFAULT false,
 ADD COLUMN handoff_selector jsonb,
 ADD COLUMN previous_worker_node_id uuid REFERENCES cluster_nodes(id),
 ADD COLUMN plugin_configuration_snapshot bytea,
 ADD CONSTRAINT execution_work_handoff_shape CHECK (
   NOT handoff_pending OR (worker_node_id IS NULL AND lease_until IS NULL
     AND handoff_selector IS NOT NULL AND previous_worker_node_id IS NOT NULL));

ALTER TABLE execution_steps
 ADD COLUMN completed_epoch bigint,
 ADD COLUMN completed_worker_node_id uuid REFERENCES cluster_nodes(id);

CREATE INDEX ix_execution_work_handoff ON execution_work(available_at)
 WHERE handoff_pending;
