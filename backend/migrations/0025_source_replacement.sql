CREATE TABLE library_replacement_policies (
 library_id uuid PRIMARY KEY REFERENCES libraries(id) ON DELETE CASCADE,
 enabled boolean NOT NULL DEFAULT false,
 grace_period_hours integer NOT NULL DEFAULT 168 CHECK(grace_period_hours BETWEEN 1 AND 8760),
 revision integer NOT NULL DEFAULT 1
);
ALTER TABLE execution_work ADD COLUMN replacement_grace_hours integer CHECK(replacement_grace_hours BETWEEN 1 AND 8760);
CREATE TABLE source_replacements (
 execution_id uuid PRIMARY KEY REFERENCES executions(id) ON DELETE RESTRICT,
 library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE RESTRICT,
 source_node_id uuid NOT NULL REFERENCES cluster_nodes(id),
 original_path text NOT NULL,
 final_path text,
 backup_path text,
 source_sha varchar(64) NOT NULL,
 result_sha varchar(64) NOT NULL,
 state text NOT NULL CHECK(state IN ('replaced','rollback_requested','rolled_back','finalized','retained','failed')),
 detail text,
 replaced_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 grace_until timestamptz,
 finished_at timestamptz,
 CHECK((state='failed') OR (final_path IS NOT NULL AND backup_path IS NOT NULL AND grace_until IS NOT NULL))
);
CREATE INDEX ix_source_replacements_due ON source_replacements(source_node_id,state,grace_until);
CREATE TABLE media_replacement_marks (
 library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
 relative_path text NOT NULL,
 byte_length bigint NOT NULL,
 modified_at timestamptz NOT NULL,
 execution_id uuid NOT NULL REFERENCES executions(id),
 PRIMARY KEY(library_id,relative_path,byte_length,modified_at)
);
