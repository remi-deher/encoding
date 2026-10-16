"""Appelle l'API locale (loopback 8081) de l'agent d'un conteneur, avec le secret local de l'image (architecture 0044).
Usage : python3 agent.py <service> GET|POST <chemin> [json]"""
import subprocess, sys, os
svc, method, path = sys.argv[1:4]; body = sys.argv[4] if len(sys.argv) > 4 else ""
code = r'''
import sys, json, urllib.request, urllib.error
sec = open("/var/lib/encoding-node/local-enrollment.secret").read().strip()
m, p, b = sys.argv[1:4]
req = urllib.request.Request("http://127.0.0.1:8081"+p, data=(b.encode() if b else None), method=m,
    headers={"X-Local-Enrollment-Secret": sec, **({"Content-Type":"application/json"} if b else {})})
try:
    r = urllib.request.urlopen(req, timeout=30); print(r.status, r.read().decode())
except urllib.error.HTTPError as e: print(e.code, e.read().decode())
'''
print(subprocess.check_output(["docker","exec",f"{os.environ.get('COMPOSE_PROJECT_NAME','encoding-cluster-test')}-{svc}-1","python3","-c",code,method,path,body],text=True).strip())
