"""Bibliothèque 'test-transfer' : node1 accès direct, node3 transfert WebDAV depuis node1, node2 non pris en charge. Vérifie la répartition."""
import os, sys, json, time, subprocess
sys.path.insert(0, '.')
from cl import *
st = json.load(open(os.environ.get('STATE_FILE', '/tmp/enc-cluster-state.json')))
a = Api('node1'); a.login()
s, lib = a.post('/api/v1/libraries', {"name": "test-transfer"}); lid = lib['id']; ids = st['nodes']
acc = [{"nodeId": ids['node1'], "mode": "Direct", "localPath": "/media/lib", "canRead": True, "canWrite": True, "transferSourceNodeId": None, "isEnabled": True},
       {"nodeId": ids['node2'], "mode": "Unsupported", "localPath": None, "canRead": False, "canWrite": False, "transferSourceNodeId": None, "isEnabled": True},
       {"nodeId": ids['node3'], "mode": "Transfer", "localPath": None, "canRead": True, "canWrite": True, "transferSourceNodeId": ids['node1'], "isEnabled": True}]
print("node-access ->", a.put(f'/api/v1/libraries/{lid}/node-access', {"nodes": acc})[0], str(a.get(f'/api/v1/libraries/{lid}/node-access')[1])[:0])
s, w = a.get(f'/api/v1/libraries/{lid}/workflow')
print("assign ->", a.put(f'/api/v1/libraries/{lid}/flow', {"flowId": st['flowId'], "version": 1, "expectedRevision": w['revision']})[0])
print("discover ->", a.post(f'/api/v1/libraries/{lid}/discover')[0]); time.sleep(8)
s, w = a.get(f'/api/v1/libraries/{lid}/workflow'); print("scan:", w['scan']['state'], len(w['files']), "fichiers")
want = {'clip1.mkv', 'clip2.mkv', 'clip3.mp4', 'clip4.mp4', 'clip5.mkv', 'clip6.mp4'}
fids = [f['id'] for f in w['files'] if f['relativePath'] in want]
s, r = a.post(f'/api/v1/libraries/{lid}/executions', {"fileIds": fids, "assignmentRevision": w['revision']}); print("enqueue ->", s, r)
def q(sql): return subprocess.check_output(["./psql.sh", sql], text=True).strip().splitlines()
for _ in range(60):
    if q(f"SELECT count(*) FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE w.library_id='{lid}' AND e.state IN ('queued','running')")[2].strip() == '0': break
    time.sleep(2)
print("répartition par worker:", [x.strip() for x in q(f"SELECT coalesce(n.name,'-'), e.state, count(*) FROM executions e JOIN execution_work w ON w.execution_id=e.id LEFT JOIN cluster_nodes n ON n.id=w.worker_node_id WHERE w.library_id='{lid}' GROUP BY 1,2 ORDER BY 1")[2:-1]])
print("nœud source des tâches de node3:", [x.strip() for x in q(f"SELECT so.name, count(*) FROM execution_work w JOIN cluster_nodes wn ON wn.id=w.worker_node_id LEFT JOIN cluster_nodes so ON so.id=w.source_node_id WHERE w.library_id='{lid}' AND wn.name='node3' GROUP BY 1")[2:-1]])
