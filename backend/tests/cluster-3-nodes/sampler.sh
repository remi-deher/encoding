#!/usr/bin/env bash
# Échantillonne toutes les secondes l'état des exécutions/baux dans PostgreSQL vers un fichier TSV. Usage : sampler.sh <fichier> [secondes]
out="${1:?fichier}"; dur="${2:-120}"; end=$((SECONDS+dur))
: > "$out"
while [ $SECONDS -lt $end ]; do
  docker exec -i encoding-cluster-test-postgres-1 psql -U encoding -d encoding -At -F $'\t' -c "SELECT to_char(clock_timestamp(),'HH24:MI:SS'), e.media, e.state, coalesce(n.name,'-'), w.lease_epoch, coalesce(round(extract(epoch from (w.lease_until-clock_timestamp()))::numeric),-1), coalesce(e.progress,0), e.attempt, left(e.id::text,8), (SELECT count(*) FROM media_reservations r WHERE r.execution_id=e.id) FROM executions e JOIN execution_work w ON w.execution_id=e.id LEFT JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE e.state NOT IN ('succeeded','failed','cancelled','deferred') ORDER BY e.media" >> "$out" 2>/dev/null
  sleep 1
done
