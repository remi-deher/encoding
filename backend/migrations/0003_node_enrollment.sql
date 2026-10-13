ALTER TABLE cluster_nodes ADD COLUMN node_token_hash bytea;

CREATE TABLE cluster_identity (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
    name varchar(128) NOT NULL DEFAULT 'Encoding'
);

INSERT INTO cluster_identity (singleton) VALUES (true);
