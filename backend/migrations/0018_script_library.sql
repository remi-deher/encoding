CREATE TABLE flow_scripts (
    id uuid PRIMARY KEY,
    draft_revision integer NOT NULL DEFAULT 1 CHECK (draft_revision > 0),
    draft_name text NOT NULL CHECK (length(draft_name) BETWEEN 1 AND 128),
    draft_language text NOT NULL CHECK (draft_language IN ('javascript', 'python')),
    draft_description text NOT NULL DEFAULT '' CHECK (length(draft_description) <= 1000),
    draft_code text NOT NULL DEFAULT '' CHECK (length(draft_code) <= 200000),
    published_version integer NOT NULL DEFAULT 0 CHECK (published_version >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX ux_flow_scripts_name ON flow_scripts (lower(draft_name));

CREATE TABLE flow_script_versions (
    script_id uuid NOT NULL REFERENCES flow_scripts(id) ON DELETE RESTRICT,
    version integer NOT NULL CHECK (version > 0),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 128),
    language text NOT NULL CHECK (language IN ('javascript', 'python')),
    description text NOT NULL DEFAULT '' CHECK (length(description) <= 1000),
    code text NOT NULL CHECK (length(code) BETWEEN 1 AND 200000),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (script_id, version)
);
