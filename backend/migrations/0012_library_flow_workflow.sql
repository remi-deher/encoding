ALTER TABLE libraries ADD COLUMN flow_assignment_revision integer NOT NULL DEFAULT 0;

CREATE TABLE library_flow_assignments (
 library_id uuid PRIMARY KEY REFERENCES libraries(id) ON DELETE CASCADE,
 flow_id uuid NOT NULL,
 flow_version integer NOT NULL,
 revision integer NOT NULL DEFAULT 1,
 FOREIGN KEY(flow_id,flow_version) REFERENCES flow_versions(flow_id,version)
);
CREATE TABLE library_scans (
 id uuid PRIMARY KEY,
 library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
 media_type text NOT NULL,
 state text NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','running','completed','failed')),
 node_id uuid REFERENCES cluster_nodes(id),
 source_root text,
 epoch bigint NOT NULL DEFAULT 0,
 lease_until timestamptz,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 completed_at timestamptz,
 detail text NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX ix_library_scan_active ON library_scans(library_id) WHERE state IN ('queued','running');
CREATE TABLE library_media (
 id uuid PRIMARY KEY,
 library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
 relative_path text NOT NULL,
 byte_length bigint NOT NULL CHECK(byte_length>=0),
 modified_at timestamptz NOT NULL,
 scan_id uuid NOT NULL REFERENCES library_scans(id),
 UNIQUE(library_id,relative_path)
);
