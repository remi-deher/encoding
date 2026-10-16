"""Met en file des fichiers de la bibliothèque test-lib. Usage : enqueue.py [fichier ...]  (tous les fichiers détectés par défaut)"""
import os, sys, json
sys.path.insert(0, '.')
from cl import *
st = json.load(open(os.environ.get('STATE_FILE', '/tmp/enc-cluster-state.json')))
a = Api(sys.argv[1] if len(sys.argv) > 1 and sys.argv[1].startswith('node') else 'node1'); a.login()
want = [x for x in sys.argv[1:] if not x.startswith('node')]
s, w = a.get(f"/api/v1/libraries/{st['libraryId']}/workflow")
files = [f for f in w['files'] if not want or f['relativePath'] in want]
s, r = a.post(f"/api/v1/libraries/{st['libraryId']}/executions", {"fileIds": [f['id'] for f in files], "assignmentRevision": w['revision']})
print(s, json.dumps(r)[:400])
