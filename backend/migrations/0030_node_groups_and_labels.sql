ALTER TABLE cluster_nodes
    ADD COLUMN display_name text,
    ADD COLUMN labels jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(labels) = 'object'),
    ADD COLUMN inventory jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(inventory) = 'object');
ALTER TABLE node_enrollment_requests
    ADD COLUMN inventory jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(inventory) = 'object');

CREATE TABLE node_groups (
    id uuid PRIMARY KEY,
    name text NOT NULL UNIQUE CHECK (length(trim(name)) BETWEEN 1 AND 128),
    description text NOT NULL DEFAULT '' CHECK (length(description) <= 2000),
    mode text NOT NULL CHECK (mode IN ('manual', 'automatic')),
    criteria jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(criteria) = 'object'),
    labels jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(labels) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE node_group_members (
    group_id uuid NOT NULL REFERENCES node_groups(id) ON DELETE CASCADE,
    node_id uuid NOT NULL REFERENCES cluster_nodes(id) ON DELETE CASCADE,
    PRIMARY KEY (group_id, node_id)
);
CREATE INDEX ix_node_group_members_node ON node_group_members(node_id);
