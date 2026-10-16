"""Test C : N requêtes de mise en file simultanées (réparties sur plusieurs serveurs) pour les mêmes fichiers valides."""
import os, sys, json, threading
sys.path.insert(0, '.')
from cl import *
st = json.load(open(os.environ.get('STATE_FILE', '/tmp/enc-cluster-state.json')))
servers = sys.argv[1:] or ['node1', 'node2']
apis = []
for i in range(8):
    a = Api(servers[i % len(servers)]); a.login(); apis.append(a)
s, w = apis[0].get(f"/api/v1/libraries/{st['libraryId']}/workflow")
ids = [f['id'] for f in w['files'] if f['relativePath'].startswith('clip')]
res, bar = [None] * 8, threading.Barrier(8)
def go(i):
    bar.wait(); res[i] = apis[i].post(f"/api/v1/libraries/{st['libraryId']}/executions", {"fileIds": ids, "assignmentRevision": w['revision']})
th = [threading.Thread(target=go, args=(i,)) for i in range(8)]
[t.start() for t in th]; [t.join() for t in th]
print("réponses:", [(servers[i % len(servers)], r[0], r[1]) for i, r in enumerate(res)])
print("total mis en file:", sum(r[1].get('queued', 0) for r in res), "| ignorés (doublons):", sum(r[1].get('skipped', 0) for r in res))
