"""Crée via l'API existante : un flow d'encodage H.264 léger (plan FFmpeg), une bibliothèque 'test-lib' avec accès direct des 3 nœuds
(chemins locaux différents pour node3), l'attribution du flow et une découverte. Écrit l'état dans state.json (ids)."""
import os, sys, json, time, uuid
sys.path.insert(0, '.')
from cl import *
def comp(cid, v="1.0.0"): return {"pluginId": "base", "pluginVersion": "1.0.0", "componentId": cid, "componentVersion": v, "configurationSchemaVersion": 1}
fid = str(uuid.uuid4())
nodes = [
 {"id": "input", "name": "Fichier vidéo", "component": comp("base-input-video"), "configuration": {}},
 {"id": "prep", "name": "Préparer", "component": comp("base-prepare-processing"), "configuration": {"tool": "ffmpeg", "preserveUnchangedTracks": True}},
 {"id": "enc", "name": "Encoder H.264", "component": comp("base-transcode-video"), "configuration": {"processingMode": "plan", "encoderMode": "automatic", "videoCodec": "h264", "quality": 30, "preset": "fast", "hardwareAcceleration": "cpu"}},
 {"id": "run", "name": "Exécuter", "component": comp("base-execute-processing"), "configuration": {"forceProcessing": True}},
 {"id": "done", "name": "Terminer", "component": comp("base-complete-flow"), "configuration": {"result": "success", "message": "Encodage de test terminé."}},
]
conns = [{"id": f"c{i}", "sourceNodeId": a, "sourceOutputId": "success", "targetNodeId": b} for i, (a, b) in enumerate([("input","prep"),("prep","enc"),("enc","run"),("run","done")])]
flow = {"formatVersion": 1, "id": fid, "version": 1, "name": "Test cluster — encodage H.264", "mediaType": "video", "nodes": nodes, "connections": conns}
a = Api('node1'); a.login()
s, r = a.post('/api/v1/flows/validate', flow); print("validate:", s, str(r)[:300])
s, r = a.put(f'/api/v1/flows/{fid}', {"expectedVersion": 0, "definition": flow}); print("save flow:", s, str(r)[:200])
s, lib = a.post('/api/v1/libraries', {"name": "test-lib"}); print("library:", s, lib); lid = lib['id']
s, n = a.get('/api/v1/nodes'); ids = {x['name']: x['id'] for x in n}
acc = [{"nodeId": ids['node1'], "mode": "Direct", "localPath": "/media/lib", "canRead": True, "canWrite": True, "transferSourceNodeId": None, "isEnabled": True},
       {"nodeId": ids['node2'], "mode": "Direct", "localPath": "/media/lib", "canRead": True, "canWrite": True, "transferSourceNodeId": None, "isEnabled": True},
       {"nodeId": ids['node3'], "mode": "Direct", "localPath": "/mnt/shared-media", "canRead": True, "canWrite": True, "transferSourceNodeId": None, "isEnabled": True}]
s, r = a.put(f'/api/v1/libraries/{lid}/node-access', {"nodes": acc}); print("node-access:", s, str(r)[:300])
s, w = a.get(f'/api/v1/libraries/{lid}/workflow'); print("workflow:", s, {k: w[k] for k in ('revision',)})
s, r = a.put(f'/api/v1/libraries/{lid}/flow', {"flowId": fid, "version": 1, "expectedRevision": w['revision']}); print("assign:", s, str(r)[:200])
s, r = a.post(f'/api/v1/libraries/{lid}/discover'); print("discover:", s, str(r)[:200])
json.dump({"flowId": fid, "libraryId": lid, "nodes": ids}, open(os.environ.get('STATE_FILE', '/tmp/enc-cluster-state.json'), 'w'))
for _ in range(40):
    time.sleep(2); s, w = a.get(f'/api/v1/libraries/{lid}/workflow')
    if w['scan'] and w['scan']['state'] not in ('queued', 'running', 'pending', 'claimed'): break
print("scan:", w['scan'], "fichiers:", sorted(f['relativePath'] for f in w['files']))
