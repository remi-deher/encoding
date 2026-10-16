"""Forme le cluster de test à partir de conteneurs démarrés (compose up) : premier administrateur (jeton d'installation),
attente de node1 et node2 (enrôlement d'office sur la base commune), jumelage de node3 par clé approuvée par l'administrateur."""
import os, sys, json, time, subprocess
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from cl import *
def wait(cond, what, timeout=180):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if cond(): return
        except Exception: pass
        time.sleep(2)
    sys.exit(f"TIMEOUT: {what}")
def agent(svc, m, p, b=None):
    here = os.path.dirname(os.path.abspath(__file__))
    out = subprocess.check_output([sys.executable, os.path.join(here, "agent.py"), svc, m, p] + ([json.dumps(b)] if b else []), text=True).strip()
    code, _, body = out.partition(' ')
    return int(code), (json.loads(body) if body[:1] in '{[' else body)
a = Api('node1')
wait(lambda: a.get('/health/live')[0] == 200, "node1 démarré")
s, r = a.post('/api/v1/auth/setup', {"username": USER, "password": PASSWORD, "setupToken": os.environ["SETUP_TOKEN"], "nodeDisplayName": "Nœud 1"})
print("setup admin:", s); assert s in (201, 409), r
a.login()
names = lambda: {n['name']: n for n in a.get('/api/v1/nodes')[1]}
wait(lambda: {'node1', 'node2'} <= set(names()) and all(names()[n]['status'] == 'online' for n in ('node1', 'node2')), "node1 et node2 enrôlés")
print("node1/node2 en ligne:", {k: v['id'][:8] for k, v in names().items()})
cid = a.get('/api/v1/cluster')[1]['id']
wait(lambda: agent('node3', 'GET', '/api/v1/worker/clusters')[0] == 200, "agent node3 prêt")
if 'node3' not in names():
    code, pk = agent('node3', 'POST', '/api/v1/worker/pairing-key', {"clusterId": cid}); assert code == 200, pk
    pend = a.get('/api/v1/nodes/enrollment-requests')[1]
    mine = agent('node3', 'GET', '/api/v1/worker/status')[1]
    shown = [p.get('verificationCode') for p in pend if p['name'] == 'node3']
    print("code de vérification: cluster", shown, "| nœud", mine.get('verificationCode'))
    assert shown and shown[0] == mine.get('verificationCode'), "codes de vérification différents"
    print("approbation:", a.post('/api/v1/nodes/enrollment-requests/approve', {"pairingKey": pk['pairingKey']})[0])
wait(lambda: names().get('node3', {}).get('status') == 'online', "node3 en ligne")
print("cluster formé:", {k: (v['id'][:8], '+'.join(v['roles'])) for k, v in names().items()})
