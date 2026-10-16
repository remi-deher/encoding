"""Test D : détacher puis rattacher node3 par le parcours prévu (départ refusé pendant un bail, départ propre, révocation, nouveau jumelage)."""
import os, sys, json, time, subprocess, urllib.request, urllib.error
sys.path.insert(0, '.')
from cl import *
P = os.environ.get("COMPOSE_PROJECT_NAME", "encoding-cluster-test")
def agent(m, p, b=None):
    out = subprocess.check_output(["python3", "agent.py", "node3", m, p] + ([json.dumps(b)] if b else []), text=True).strip()
    code, _, body = out.partition(' ')
    try: body = json.loads(body)
    except Exception: pass
    return int(code), body
def sql(q): return subprocess.check_output(["./psql.sh", q], text=True).strip().splitlines()
def step(msg): print(f"\n### {msg}", flush=True)
st = json.load(open(os.environ.get('STATE_FILE', '/tmp/enc-cluster-state.json')))
a = Api('node1'); a.login()
lib = st['libraryId']

RESUME = "--resume" in sys.argv
if not RESUME:
  step("D1 : départ demandé pendant un traitement (attendu : refus 409)")
  old = agent("GET", "/api/v1/worker/status")[1]; old_id = old['nodeId']
  tok = json.loads(subprocess.check_output(["docker", "exec", f"{P}-node3-1", "cat", "/var/lib/encoding-node/node-identity.json"], text=True))
  old_token = tok.get('nodeToken') or tok.get('token') or [v for k, v in tok.items() if 'oken' in k][0]
  print("ancien id:", old_id)
  subprocess.run(["python3", "enqueue.py", "long1.mkv", "long2.mkv", "long3.mkv"], check=True, capture_output=True)
  for _ in range(40):
      if sql(f"SELECT count(*) FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.state='running' AND w.worker_node_id='{old_id}'")[2].strip() != '0': break
      time.sleep(1)
  time.sleep(3)
  code, body = agent("POST", "/api/v1/worker/leave"); print("leave pendant un bail ->", code, str(body)[:200])
  D1 = code
  for _ in range(60):
      if sql("SELECT count(*) FROM executions WHERE state IN ('running','queued')")[2].strip() == '0': break
      time.sleep(2)

  step("D2 : départ propre")
  code, body = agent("POST", "/api/v1/worker/leave"); print("leave ->", code, str(body)[:200])
  D2 = code
  time.sleep(3)
  s2 = agent("GET", "/api/v1/worker/status")[1]
  print("après départ : nouvel id =", s2['nodeId'], "| enrôlé =", s2['isEnrolled'], "| cluster =", s2.get('clusterId'))
  print("nouvel id différent de l'ancien :", s2['nodeId'] != old_id)
  print("ligne ancienne identité (deleted_at non nul) :", sql(f"SELECT deleted_at IS NOT NULL FROM cluster_nodes WHERE id='{old_id}'")[2].strip())
  print("accès bibliothèque de l'ancien id restants :", sql(f"SELECT count(*) FROM library_node_accesses WHERE node_id='{old_id}'")[2].strip())
  req = urllib.request.Request(f"http://{ip('node1')}:8080/api/v1/nodes/{old_id}/heartbeat", method="PUT", headers={"X-Node-Token": old_token, "Content-Type": "application/json"},
      data=json.dumps({"name": "node3", "roles": ["worker"], "maxConcurrentTasks": 1, "capabilities": []}).encode())
  try: r = urllib.request.urlopen(req, timeout=10); hb = r.status
  except urllib.error.HTTPError as e: hb = e.code
  print("heartbeat avec l'ancien jeton ->", hb, "(attendu : refus)")
  time.sleep(5)
  s, n = a.get('/api/v1/nodes'); print("nœuds visibles:", [(x['name'], x['id'][:8], x['status']) for x in n])

if RESUME:
    old_id = "bba88d9a-ef08-4e81-9bd9-5da545f36867"; D1 = D2 = hb = "(joué avant reprise)"
step("D3 : nouveau jumelage (clé + approbation admin)")
cid = a.get('/api/v1/cluster')[1]['id']
code, pk = agent("POST", "/api/v1/worker/pairing-key", {"clusterId": cid}); print("clé ->", code)
s, r = a.post('/api/v1/nodes/enrollment-requests/approve', {"pairingKey": pk['pairingKey']}); print("approbation ->", s)
time.sleep(12)
s, n = a.get('/api/v1/nodes'); ids = {x['name']: x['id'] for x in n}; print("nœuds visibles:", [(x['name'], x['id'][:8], x['status']) for x in n])
new_id = ids['node3']; print("node3 = nouvel id:", new_id != old_id)
st['nodes'] = ids; json.dump(st, open(os.environ.get('STATE_FILE', '/tmp/enc-cluster-state.json'), 'w'))

step("D4 : sans accès configuré, node3 ne reçoit aucune tâche")
subprocess.run(["python3", "enqueue.py", "clip1.mkv", "clip2.mkv", "clip3.mp4", "clip4.mp4", "clip5.mkv", "clip6.mp4"], check=True, capture_output=True)
for _ in range(60):
    if sql("SELECT count(*) FROM executions WHERE state IN ('running','queued')")[2].strip() == '0': break
    time.sleep(2)
rows = sql(f"SELECT coalesce(n.name,'?'), count(*) FROM executions e JOIN execution_work w ON w.execution_id=e.id LEFT JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE e.media LIKE 'clip%' AND e.queued_at > now()-interval '3 minutes' GROUP BY 1 ORDER BY 1")[2:-1]
print("répartition (nom|nb):", [r.strip() for r in rows])

step("D5 : accès restauré -> node3 participe à nouveau")
acc = [{"nodeId": ids['node1'], "mode": "Direct", "localPath": "/media/lib", "canRead": True, "canWrite": True, "transferSourceNodeId": None, "isEnabled": True},
       {"nodeId": ids['node2'], "mode": "Direct", "localPath": "/media/lib", "canRead": True, "canWrite": True, "transferSourceNodeId": None, "isEnabled": True},
       {"nodeId": ids['node3'], "mode": "Direct", "localPath": "/mnt/shared-media", "canRead": True, "canWrite": True, "transferSourceNodeId": None, "isEnabled": True}]
print("node-access ->", a.put(f'/api/v1/libraries/{lib}/node-access', {"nodes": acc})[0])
subprocess.run(["python3", "enqueue.py", "clip1.mkv", "clip2.mkv", "clip3.mp4", "clip4.mp4", "clip5.mkv", "clip6.mp4"], check=True, capture_output=True)
for _ in range(60):
    if sql("SELECT count(*) FROM executions WHERE state IN ('running','queued')")[2].strip() == '0': break
    time.sleep(2)
rows = sql(f"SELECT coalesce(n.name,'?'), count(*) FROM executions e JOIN execution_work w ON w.execution_id=e.id LEFT JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE e.media LIKE 'clip%' AND e.queued_at > now()-interval '90 seconds' GROUP BY 1 ORDER BY 1")[2:-1]
print("répartition après restauration (nom|nb):", [r.strip() for r in rows])
print("RÉSUMÉ D: leave_pendant_bail=%s leave_propre=%s heartbeat_ancien_jeton=%s" % (D1, D2, hb))
