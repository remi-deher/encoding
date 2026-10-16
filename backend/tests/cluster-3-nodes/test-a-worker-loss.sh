#!/usr/bin/env bash
# Test A : perte brutale (SIGKILL) du conteneur node3 pendant des traitements. Chronologie dans le fichier passé en argument.
set -u; cd "$(dirname "$0")"; out="${1:?fichier journal}"; P="${COMPOSE_PROJECT_NAME:-encoding-cluster-test}"
t0=$SECONDS; log(){ printf '[+%03ds %s] %s\n' $((SECONDS-t0)) "$(date +%T)" "$*" | tee -a "$out"; }
: > "$out"
./sampler.sh "${out%.log}-samples.tsv" 420 & 
python3 enqueue.py $(for i in 1 2 3 4 5 6; do echo long$i.mkv; done) | while read l; do log "enqueue: $l"; done
for _ in $(seq 1 60); do
  n3=$(docker exec -i $P-postgres-1 psql -U encoding -d encoding -At -c "SELECT count(*) FROM executions e JOIN execution_work w ON w.execution_id=e.id JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE e.state='running' AND n.name='node3' AND e.media LIKE 'long%'")
  [ "$n3" -ge 1 ] && break; sleep 1
done
sleep 8   # laisser le traitement avancer
victim=$(docker exec -i $P-postgres-1 psql -U encoding -d encoding -At -c "SELECT e.media||' '||left(e.id::text,8)||' epoch='||w.lease_epoch FROM executions e JOIN execution_work w ON w.execution_id=e.id JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE e.state='running' AND n.name='node3'")
log "AVANT KILL: node3 traite : $victim"
docker kill $P-node3-1 >/dev/null; log "node3 TUÉ (SIGKILL)"
off=""; for _ in $(seq 1 120); do
  st=$(python3 -c "
import sys; sys.path.insert(0,'.')
from cl import *
a=Api('node1'); a.login(); print([ (n['status'],n.get('lifecycle')) for n in a.get('/api/v1/nodes')[1] if n['name']=='node3'][0])")
  [ -z "$off" ] && echo "$st" | grep -q offline && { off=1; log "node3 vu hors ligne par le cluster : $st"; }
  fin=$(docker exec -i $P-postgres-1 psql -U encoding -d encoding -At -c "SELECT count(*) FROM executions WHERE media LIKE 'long%' AND state IN ('succeeded','failed','cancelled')")
  [ "$fin" -ge 6 ] && break; sleep 3
done
log "fin d'observation : $fin/6 terminées"
