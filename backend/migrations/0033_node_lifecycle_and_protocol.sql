-- États de cycle de vie des nœuds et compatibilité de versions (architecture 0041).
-- draining : vidange demandée (aucune nouvelle tâche, les baux en cours se terminent) ;
-- quarantined : identité utilisée par deux processus à la fois, levée par un administrateur ;
-- incompatible_reason : protocole hors de la plage acceptée par le cluster.
ALTER TABLE cluster_nodes
    ADD COLUMN draining boolean NOT NULL DEFAULT false,
    ADD COLUMN quarantined boolean NOT NULL DEFAULT false,
    ADD COLUMN incompatible_reason text,
    ADD COLUMN protocol_version integer,
    ADD COLUMN session_nonce text,
    ADD COLUMN previous_nonce text,
    ADD COLUMN nonce_changed_at timestamptz,
    ADD COLUMN accepts_work boolean GENERATED ALWAYS AS (NOT draining AND NOT quarantined AND incompatible_reason IS NULL) STORED;
