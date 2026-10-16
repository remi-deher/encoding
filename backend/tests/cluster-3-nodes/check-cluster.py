"""Contrôle de cohérence du cluster : vue de chaque serveur, rôles, outils, inventaire, identités. Sortie compacte, code de sortie 1 si incohérence."""
import sys, json, subprocess
sys.path.insert(0, '.')
from cl import *
servers = [s for s in sys.argv[1:] or ['node1', 'node2']]
views, bad = {}, []
for s in servers:
    try:
        a = Api(s); a.login()
        st, nodes = a.get('/api/v1/nodes'); assert st == 200, (st, nodes)
        views[s] = {n['id']: n for n in nodes}
        print(f"[{s}] cluster={a.get('/api/v1/cluster')[1].get('id')} nœuds={len(nodes)}")
    except Exception as e:
        print(f"[{s}] INJOIGNABLE: {e}"); bad.append(f"{s} injoignable")
ids = [set(v) for v in views.values()]
if ids and any(i != ids[0] for i in ids): bad.append("vues divergentes entre serveurs")
if views:
    for nid, n in next(iter(views.values())).items():
        inv = n.get('inventory') or {}
        tools = sorted(c['toolId'] for c in n['capabilities'] if not c['toolId'].startswith('plugin/'))
        usable = sorted({e['encoderId'] for c in n['capabilities'] for e in (c.get('encoderChecks') or []) if e['status'] == 'usable'})
        print(f"  {n['name']:6} {nid[:8]} roles={'+'.join(n['roles'])} {n['status']}/{n.get('lifecycle')} slots={n['maxConcurrentTasks']} cpu={inv.get('processorCount')} mem={inv.get('memoryMb')}Mo os={inv.get('operatingSystem')}")
        print(f"         outils={','.join(tools)} encodeurs_utilisables={','.join(usable)}")
names = [n['name'] for n in next(iter(views.values())).values()] if views else []
if len(names) != len(set(names)): bad.append("noms dupliqués")
q = subprocess.check_output(["./psql.sh", "SELECT count(*) total, count(DISTINCT id) ids, count(DISTINCT node_token_hash) tokens, count(*) FILTER (WHERE deleted_at IS NOT NULL) supprimes, count(*) FILTER (WHERE quarantined) quarantaine, count(*) FILTER (WHERE incompatible_reason IS NOT NULL) incompat FROM cluster_nodes"], text=True)
print(q.strip())
print("RÉSULTAT:", "OK" if not bad else "INCOHÉRENCES " + "; ".join(bad)); sys.exit(1 if bad else 0)
