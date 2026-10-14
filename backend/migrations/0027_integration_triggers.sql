-- Demandes de traitement reçues d’un service externe (webhook d’import Sonarr/Radarr).
-- Une demande attend la prochaine découverte complète de sa bibliothèque, puis crée l’exécution du flow attribué
-- par le chemin commun de mise en file des bibliothèques (réservation du média et reçus d’empreinte inclus).
CREATE TABLE integration_trigger_requests (
 id uuid PRIMARY KEY,
 plugin_id text NOT NULL,
 library_id uuid NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
 relative_path text NOT NULL,
 state text NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','processing','queued','skipped','expired')),
 detail text NOT NULL DEFAULT '',
 requested_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 claimed_until timestamptz,
 completed_at timestamptz
);
-- Un même fichier ne peut avoir qu’une demande ouverte : une livraison répétée du webhook est sans effet.
CREATE UNIQUE INDEX ix_integration_trigger_open ON integration_trigger_requests(library_id,upper(relative_path)) WHERE state IN ('pending','processing');
CREATE INDEX ix_integration_trigger_state ON integration_trigger_requests(state,requested_at);
