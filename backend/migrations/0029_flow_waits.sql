-- Attentes durables (architecture 0033) : une ligne par exécution mise en attente par un bloc Attendre.
CREATE TABLE execution_waits (
 execution_id uuid PRIMARY KEY REFERENCES executions(id) ON DELETE RESTRICT,
 node_id text NOT NULL,
 spec jsonb NOT NULL,
 state text NOT NULL DEFAULT 'waiting' CHECK(state IN ('waiting','met','timeout','cancelled')),
 started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 deadline timestamptz,
 due_at timestamptz,
 next_check_at timestamptz NOT NULL,
 resolved_at timestamptz,
 probe_count integer NOT NULL DEFAULT 0,
 last_probe_at timestamptz,
 last_probe_output text,
 CHECK((state='waiting') = (resolved_at IS NULL))
);
CREATE INDEX ix_execution_waits_due ON execution_waits(next_check_at) WHERE state='waiting';

-- Une sonde due rend l'exécution réclamable par un worker éligible, uniquement pour évaluer la condition.
ALTER TABLE execution_work ADD COLUMN wait_probe_due boolean NOT NULL DEFAULT false;

-- Politique média choisie par le dernier bloc Attendre traversé (architecture 0033 et 0030).
ALTER TABLE execution_work ADD COLUMN media_policy text NOT NULL DEFAULT 'keep' CHECK (media_policy IN ('keep','beside','overwrite'));
