CREATE TABLE encoding_profiles (
    id uuid PRIMARY KEY,
    draft_revision integer NOT NULL DEFAULT 1 CHECK (draft_revision > 0),
    draft_name text NOT NULL CHECK (length(draft_name) BETWEEN 1 AND 128),
    draft_description text NOT NULL DEFAULT '' CHECK (length(draft_description) <= 1000),
    draft_targets jsonb NOT NULL,
    published_version integer NOT NULL DEFAULT 0 CHECK (published_version >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX ux_encoding_profiles_name ON encoding_profiles (lower(draft_name));

CREATE TABLE encoding_profile_versions (
    profile_id uuid NOT NULL REFERENCES encoding_profiles(id) ON DELETE RESTRICT,
    version integer NOT NULL CHECK (version > 0),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 128),
    description text NOT NULL DEFAULT '' CHECK (length(description) <= 1000),
    targets jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (profile_id, version)
);
