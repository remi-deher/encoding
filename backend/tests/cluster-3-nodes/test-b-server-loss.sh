#!/usr/bin/env bash
# Test B : perte brutale (SIGKILL) du conteneur node1 (serveur + worker) pendant des traitements ; node2 doit rester opérationnel.
set -u; cd "$(dirname "$0")"; out="${1:?fichier journal}"; P=encoding-cluster-test
t0=$SECONDS; log(){ printf '[+%03ds %s] %s\n' $((SECONDS-t0)) "$(date +%T)" "$*" | tee -a "$out"; }
q(){ docker exec -i $P-postgres-1 psql -U encoding -d encoding -At -c "$1"; }
: > "$out"; ./sampler.sh "${out%.log}-samples.tsv" 480 &
python3 enqueue.py node2 $(for i in 1 2 3 4 5 6; do echo long$i.mkv; done) | while read l; do log "enqueue (via node2): $l"; done
for _ in $(seq 1 60); do [ "$(q "SELECT count(*) FROM executions e JOIN execution_work w ON w.execution_id=e.id JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE e.state='running' AND n.name='node1' AND media LIKE 'long%'")" -ge 1 ] && break; sleep 1; done
sleep 6; log "AVANT KILL: $(q "SELECT string_agg(n.name||':'||e.media||'/ep'||w.lease_epoch, ' ') FROM executions e JOIN execution_work w ON w.execution_id=e.id JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE e.state='running'")"
docker kill $P-node1-1 >/dev/null; log "node1 TUÉ (SIGKILL)"
for i in $(seq 1 70); do
  st=$(python3 - <<'P'
import sys; sys.path.insert(0,'.')
from cl import *
try:
    a=Api('node2'); a.login(); s,n=a.get('/api/v1/nodes'); s2,e=a.get('/api/v1/executions')
    print(' '.join(f"{x['name']}={x['status']}" for x in n), '| exec:', {k:v for k,v in e.items() if k.endswith('Count') and k!='deferredCount'})
except Exception as ex: print("node2 API KO:", ex)
P
)
  [ $((i%3)) -eq 1 ] && log "$st"
  [ "$(q "SELECT count(*) FROM executions WHERE media LIKE 'long%' AND state IN ('succeeded','failed','cancelled')")" -ge 12 ] && break; sleep 3
done
log "état à l'issue de l'observation : $(q "SELECT string_agg(e.media||':'||e.state||'@'||coalesce(n.name,'-')||'/ep'||w.lease_epoch, ' ') FROM executions e JOIN execution_work w ON w.execution_id=e.id LEFT JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE e.state IN ('running','queued')")"
