CREATE TABLE library_node_accesses (
    library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
    node_id uuid NOT NULL REFERENCES cluster_nodes(id) ON DELETE CASCADE,
    mode varchar(16) NOT NULL CHECK (mode IN ('direct', 'unsupported', 'transfer')),
    local_path text,
    can_read boolean NOT NULL DEFAULT false,
    can_write boolean NOT NULL DEFAULT false,
    transfer_source_node_id uuid REFERENCES cluster_nodes(id) ON DELETE RESTRICT,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (library_id, node_id),
    CHECK (
        (mode = 'direct' AND local_path IS NOT NULL AND length(trim(local_path)) > 0 AND can_read AND transfer_source_node_id IS NULL)
        OR (mode = 'unsupported' AND local_path IS NULL AND NOT can_read AND NOT can_write AND transfer_source_node_id IS NULL)
        OR (mode = 'transfer' AND local_path IS NULL AND can_read AND can_write AND transfer_source_node_id IS NOT NULL AND transfer_source_node_id <> node_id)
    )
);

CREATE INDEX ix_library_node_accesses_node_id ON library_node_accesses (node_id);
