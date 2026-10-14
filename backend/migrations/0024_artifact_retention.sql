ALTER TABLE execution_work ADD COLUMN artifacts_purged_at timestamptz;
CREATE INDEX ix_execution_work_artifacts_pending ON execution_work(execution_id) WHERE artifacts_purged_at IS NULL;
