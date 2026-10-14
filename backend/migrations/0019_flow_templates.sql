CREATE TABLE flow_templates (
    id uuid PRIMARY KEY,
    name text NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 128),
    description text NOT NULL DEFAULT '' CHECK (length(description) <= 1000),
    source_flow_id uuid NOT NULL,
    source_version integer NOT NULL CHECK (source_version > 0),
    source_flow_name text NOT NULL CHECK (length(source_flow_name) BETWEEN 1 AND 128),
    media_type text NOT NULL CHECK (media_type IN ('video', 'image', 'music', 'subtitles')),
    definition jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_flow_templates_source_version
        FOREIGN KEY (source_flow_id, source_version)
        REFERENCES flow_versions(flow_id, version) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ux_flow_templates_name ON flow_templates (lower(name));
CREATE INDEX ix_flow_templates_source ON flow_templates (source_flow_id, source_version);
