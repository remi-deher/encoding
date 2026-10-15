-- Mode « accepter la première liaison » (architecture 0040) : réglage explicite d'un administrateur du cluster d'accueil,
-- à usage unique et à durée limitée. Une seule ligne ; elle disparaît à la première liaison acceptée ou à l'expiration.
CREATE TABLE first_link_acceptance (
    id boolean PRIMARY KEY DEFAULT true CHECK (id),
    expires_at timestamptz NOT NULL,
    enabled_by text NOT NULL
);
