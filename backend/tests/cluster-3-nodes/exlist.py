import sys,json; sys.path.insert(0,'.')
from cl import *
a=Api(sys.argv[1] if len(sys.argv)>1 else 'node2'); a.login(); s,ex=a.get('/api/v1/executions')
print({k:v for k,v in ex.items() if k.endswith('Count')})
for e in sorted(ex['executions'],key=lambda e:(e['media'],e['attempt'])):
    print(f"{e['media']:12} a{e['attempt']} {e['state']:10} {str(e.get('node')):6} prog={e.get('progress')} start={(e.get('startedAt') or '')[11:19]} end={(e.get('finishedAt') or '')[11:19]} | {e['detail'][:90]}")
