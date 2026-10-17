CREATE TABLE cluster_sync_identity (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    origin_id uuid NOT NULL,
    next_sequence bigint NOT NULL DEFAULT 0 CHECK (next_sequence >= 0)
);

CREATE TABLE cluster_change_log (
    event_id uuid PRIMARY KEY,
    cluster_id uuid NOT NULL,
    origin_id uuid NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    event_type text NOT NULL,
    payload jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    UNIQUE (origin_id, sequence)
);
CREATE INDEX ix_cluster_change_log_origin_sequence ON cluster_change_log (origin_id, sequence);

CREATE TABLE cluster_sync_registers (
    register_key text PRIMARY KEY,
    value jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    origin_id uuid NOT NULL,
    sequence bigint NOT NULL
);

CREATE TABLE cluster_sync_peer_vectors (
    peer_url text NOT NULL,
    origin_id uuid NOT NULL,
    sequence bigint NOT NULL CHECK (sequence >= 0),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (peer_url, origin_id)
);

-- Version courante des entités dont le contenu converge entre serveurs. Le journal reste la source
-- de transport ; cette table évite de republier un état reçu et mémorise les suppressions.
CREATE TABLE cluster_sync_entities (
    entity_type text NOT NULL,
    entity_key text NOT NULL,
    fingerprint text NOT NULL,
    occurred_at timestamptz NOT NULL,
    origin_id uuid NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    is_deleted boolean NOT NULL DEFAULT false,
    PRIMARY KEY (entity_type, entity_key)
);

-- Un événement est journalisé avant sa projection afin de pouvoir être relayé même si une référence
-- étrangère arrive plus tard. La projection est retentée après réception de ses dépendances.
CREATE TABLE cluster_sync_pending_projections (
    event_id uuid PRIMARY KEY REFERENCES cluster_change_log(event_id) ON DELETE CASCADE
);

CREATE TABLE cluster_sync_table_cursors (
    entity_type text PRIMARY KEY,
    cursor_value timestamptz NOT NULL
);

CREATE INDEX ix_library_scans_latest_completed ON library_scans(library_id, completed_at DESC, id DESC) WHERE state = 'completed';
CREATE INDEX ix_library_media_scan ON library_media(library_id, scan_id, relative_path);
