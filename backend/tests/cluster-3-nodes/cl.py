"""Client minimal de l'API Encoding pour le test 3 nœuds. Cible un conteneur par son IP sur le réseau Docker isolé.
Usage Python : from cl import Api; a=Api('node1'); a.login(); a.get('/api/v1/nodes')"""
import json, subprocess, urllib.request, urllib.error, http.cookiejar, os

PROJECT = os.environ.get("COMPOSE_PROJECT_NAME", "encoding-cluster-test")
USER, PASSWORD = "admin-test", os.environ.get("ADMIN_PASSWORD", "Tmp-Cluster-Test-2026!")

def ip(service):
    out = subprocess.check_output(["docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", f"{PROJECT}-{service}-1"], text=True).strip()
    if not out: raise RuntimeError(f"{service}: pas d'IP (conteneur arrêté ?)")
    return out

class Api:
    def __init__(self, service, port=8080):
        self.service, self.port = service, port
        self.jar = http.cookiejar.CookieJar()
        self.op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
    def call(self, method, path, body=None, headers=None, raw=False, timeout=30):
        data = None if body is None else json.dumps(body).encode()
        h = {"X-Encoding-Csrf": "1", **({"Content-Type": "application/json"} if body is not None else {}), **(headers or {})}
        req = urllib.request.Request(f"http://{ip(self.service)}:{self.port}{path}", data=data, method=method, headers=h)
        try:
            with self.op.open(req, timeout=timeout) as r:
                t = r.read()
                return r.status, (t if raw else (json.loads(t) if t.strip()[:1] in (b"{", b"[") else t.decode()))
        except urllib.error.HTTPError as e:
            t = e.read().decode(errors="replace")
            try: t = json.loads(t)
            except Exception: pass
            return e.code, t
    def get(self, p, **k): return self.call("GET", p, **k)
    def post(self, p, b=None, **k): return self.call("POST", p, b, **k)
    def put(self, p, b=None, **k): return self.call("PUT", p, b, **k)
    def delete(self, p, **k): return self.call("DELETE", p, **k)
    def login(self):
        s, r = self.post("/api/v1/auth/login", {"username": USER, "password": PASSWORD})
        assert s in (200, 204), (s, r); return r
