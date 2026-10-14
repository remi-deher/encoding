-- Authentification des utilisateurs de l’interface et de l’API de gestion.
-- Les sessions sont stockées dans PostgreSQL afin que tous les serveurs du cluster les partagent.
CREATE TABLE users (
    id uuid PRIMARY KEY,
    username text NOT NULL,
    password_hash text NOT NULL,
    role text NOT NULL CHECK (role IN ('admin', 'viewer')),
    is_active boolean NOT NULL DEFAULT true,
    failed_attempts integer NOT NULL DEFAULT 0,
    locked_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT users_username_length CHECK (char_length(username) BETWEEN 1 AND 64)
);
CREATE UNIQUE INDEX users_username_lower_idx ON users (lower(username));

CREATE TABLE user_sessions (
    token_hash bytea PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL
);
CREATE INDEX user_sessions_user_idx ON user_sessions (user_id);
CREATE INDEX user_sessions_expiry_idx ON user_sessions (expires_at);

-- Fenêtre d’enrôlement direct : une seule ligne, ouverte explicitement par un administrateur.
CREATE TABLE enrollment_window (
    id boolean PRIMARY KEY DEFAULT true CHECK (id),
    open_until timestamptz NOT NULL
);
