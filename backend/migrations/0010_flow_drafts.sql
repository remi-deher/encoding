CREATE TABLE flow_drafts (
 flow_id uuid PRIMARY KEY REFERENCES flows(id) ON DELETE RESTRICT,
 revision integer NOT NULL CHECK(revision > 0),
 definition jsonb NOT NULL,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE flow_draft_backups (
 flow_id uuid NOT NULL REFERENCES flows(id),
 revision integer NOT NULL,
 definition jsonb NOT NULL,
 status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','ready','failed')),
 checked_at timestamptz,
 next_attempt_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(flow_id,revision)
);
CREATE INDEX ix_flow_draft_backups_due ON flow_draft_backups(next_attempt_at);
CREATE TRIGGER flow_draft_snapshot_immutable BEFORE UPDATE OF definition OR DELETE ON flow_draft_backups
FOR EACH ROW EXECUTE FUNCTION protect_flow_version();
