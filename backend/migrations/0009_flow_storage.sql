CREATE TABLE flows (
 id uuid PRIMARY KEY,
 current_version integer NOT NULL DEFAULT 0,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE flow_versions (
 flow_id uuid NOT NULL REFERENCES flows(id) ON DELETE RESTRICT,
 version integer NOT NULL CHECK(version > 0),
 definition jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(flow_id, version)
);
CREATE TABLE flow_backup_jobs (
 flow_id uuid NOT NULL,
 version integer NOT NULL,
 status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','ready','failed')),
 checked_at timestamptz,
 next_attempt_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(flow_id, version),
 FOREIGN KEY(flow_id,version) REFERENCES flow_versions(flow_id,version) ON DELETE RESTRICT
);
CREATE INDEX ix_flow_backup_due ON flow_backup_jobs(next_attempt_at);
CREATE FUNCTION protect_flow_version() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'Published flow versions are immutable';
END;
$$;
CREATE TRIGGER flow_version_immutable BEFORE UPDATE OR DELETE ON flow_versions
FOR EACH ROW EXECUTE FUNCTION protect_flow_version();
