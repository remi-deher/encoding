ALTER TABLE library_node_accesses
    ADD COLUMN is_enabled boolean NOT NULL DEFAULT true;

UPDATE library_node_accesses
SET is_enabled = false
WHERE mode = 'unsupported';

ALTER TABLE library_node_accesses
    ADD CONSTRAINT ck_library_node_accesses_enabled_mode
    CHECK (mode <> 'unsupported' OR NOT is_enabled);
