
CREATE TABLE library_processing_settings (
 library_id uuid PRIMARY KEY REFERENCES libraries(id) ON DELETE CASCADE,
 watch_enabled boolean NOT NULL DEFAULT true,
 scan_interval_seconds integer NOT NULL DEFAULT 60 CHECK(scan_interval_seconds BETWEEN 15 AND 86400),
 max_concurrent_tasks integer CHECK(max_concurrent_tasks BETWEEN 1 AND 1000),
 revision integer NOT NULL DEFAULT 1
);
CREATE TABLE node_processing_settings (
 node_id uuid PRIMARY KEY REFERENCES cluster_nodes(id),
 max_concurrent_tasks integer CHECK(max_concurrent_tasks BETWEEN 1 AND 1000),
 revision integer NOT NULL DEFAULT 1
);
CREATE TABLE library_node_processing_limits (
 library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
 node_id uuid NOT NULL REFERENCES cluster_nodes(id),
 max_concurrent_tasks integer NOT NULL CHECK(max_concurrent_tasks BETWEEN 1 AND 1000),
 PRIMARY KEY(library_id,node_id)
);
ALTER TABLE library_media ADD COLUMN stable_since timestamptz NOT NULL DEFAULT clock_timestamp();
CREATE TABLE library_processing_receipts (
 library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
 file_id uuid NOT NULL REFERENCES library_media(id) ON DELETE CASCADE,
 flow_id uuid NOT NULL,
 flow_version integer NOT NULL,
 byte_length bigint NOT NULL,
 modified_at timestamptz NOT NULL,
 execution_id uuid NOT NULL REFERENCES executions(id),
 PRIMARY KEY(library_id,file_id,flow_id,flow_version,byte_length,modified_at)
);
INSERT INTO library_processing_receipts(library_id,file_id,flow_id,flow_version,byte_length,modified_at,execution_id)
SELECT DISTINCT ON (m.library_id,m.id,(e.flow_definition->>'id')::uuid,e.flow_version,m.byte_length,m.modified_at)
 m.library_id,m.id,(e.flow_definition->>'id')::uuid,e.flow_version,m.byte_length,m.modified_at,e.id
FROM library_media m JOIN execution_work w ON w.library_id=m.library_id AND w.media_path=m.relative_path
JOIN executions e ON e.id=w.execution_id
WHERE e.queued_at>=m.modified_at AND e.flow_definition ? 'id'
ORDER BY m.library_id,m.id,(e.flow_definition->>'id')::uuid,e.flow_version,m.byte_length,m.modified_at,e.queued_at DESC;
