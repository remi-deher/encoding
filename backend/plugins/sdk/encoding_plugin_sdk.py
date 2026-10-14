"""SDK Encoding pour écrire un plugin de flow en Python 3 (bibliothèque standard uniquement).

Un plugin est un processus lancé à chaque appel par le worker, dans le bac à sable Landlock. Il reçoit une
requête JSON sur une ligne de stdin ({"type":"describe"} ou {"type":"execute"}) et répond par une ligne JSON
sur stdout. Ce module implémente ce protocole (contrat 1) : version du contrat, identité du plugin, composant
déclaré, sortie déclarée. Un composant ne contient que sa logique métier.

    from encoding_plugin_sdk import Plugin, Outcome, load_manifest

    plugin = Plugin(load_manifest(__file__, "mon-plugin"))

    @plugin.component("mon-composant")
    def run(ctx):
        return Outcome.output("success", "terminé", variables={"nom": "valeur"})

    if __name__ == "__main__":
        raise SystemExit(plugin.run())

Un échec d'infrastructure (service injoignable, authentification refusée…) se signale en levant
InfrastructureError : le processus se termine en erreur et l'hôte considère l'effet incertain, sans jamais le
transformer en sortie métier. Un résultat métier attendu (élément introuvable…) se rapporte par une sortie
déclarée dans le descripteur du composant. N'écrivez ni secret ni donnée sensible dans stdout ou stderr.
"""
import json
import os
import socket
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

CONTRACT_VERSION = 1
MAX_REQUEST_CHARACTERS = 1024 * 1024
TERMINAL_RESULTS = ("unchanged", "success", "failed", "deferred")


class InfrastructureError(Exception):
    """Échec technique : le processus se termine en erreur (code 1) et l'effet est considéré comme incertain."""


class ContractError(Exception):
    """Requête ou réponse incompatible avec le contrat plugin."""


class Outcome:
    """Résultat d'un composant : exactement une sortie déclarée ou un résultat terminal."""

    def __init__(self, output_id=None, terminal=None, detail="", variables=None):
        self.output_id = output_id
        self.terminal = terminal
        self.detail = detail
        self.variables = variables or {}

    @staticmethod
    def output(output_id, detail="", variables=None):
        return Outcome(output_id=output_id, detail=detail, variables=variables)

    @staticmethod
    def finish(terminal, detail=""):
        if terminal not in TERMINAL_RESULTS:
            raise ValueError("Résultat terminal inconnu : %s" % terminal)
        return Outcome(terminal=terminal, detail=detail)


class Context:
    """Invocation reçue par un composant, avec accès typé à sa configuration et à celle du plugin."""

    def __init__(self, invocation):
        self.invocation = invocation
        self.config = invocation.get("configuration") or {}
        self.plugin_config = invocation.get("pluginConfiguration") or {}
        context = invocation.get("context") or {}
        self.media_path = context.get("mediaPath")
        self.library_id = context.get("libraryId")
        self.variables = context.get("variables") or {}

    @staticmethod
    def _text(values, key):
        value = values.get(key)
        return value if isinstance(value, str) and value.strip() else None

    @staticmethod
    def _number(values, key):
        value = values.get(key)
        return value if isinstance(value, (int, float)) and not isinstance(value, bool) else None

    def text(self, key):
        """Paramètre texte de l'instance du composant dans le flow, ou None."""
        return self._text(self.config, key)

    def number(self, key):
        return self._number(self.config, key)

    def flag(self, key, default=False):
        value = self.config.get(key)
        return value if isinstance(value, bool) else default

    def plugin_text(self, key):
        """Configuration effective du plugin (valeurs globales fusionnées avec les dérogations du flow)."""
        return self._text(self.plugin_config, key)

    def plugin_number(self, key):
        return self._number(self.plugin_config, key)


def load_manifest(script_path, plugin_id):
    """Charge le manifeste du plugin.

    Le serveur et le plugin utilisent le même JSON : il est cherché dans manifests/<id>.json à côté du script
    (image worker), puis dans le catalogue du dépôt (développement).
    """
    here = os.path.dirname(os.path.abspath(script_path))
    candidates = [
        os.path.join(here, "manifests", plugin_id + ".json"),
        os.path.join(here, plugin_id + ".json"),
        os.path.join(here, "..", "..", "src", "Encoding.Api", "Flows", "Catalog", plugin_id + ".json"),
    ]
    for path in candidates:
        if os.path.isfile(path):
            with open(path, encoding="utf-8") as handle:
                return json.load(handle)
    raise ContractError("Manifeste introuvable pour le plugin %s." % plugin_id)


class Plugin:
    def __init__(self, manifest):
        if manifest.get("contractVersion") != CONTRACT_VERSION or CONTRACT_VERSION not in manifest.get("supportedHostContractVersions", []):
            raise ContractError("Le manifeste doit déclarer le contrat hôte %d." % CONTRACT_VERSION)
        self.manifest = manifest
        self._handlers = {}

    def component(self, component_id):
        """Décorateur : associe une fonction ctx -> Outcome à un composant du manifeste."""
        def register(handler):
            if component_id not in {c["id"] for c in self.manifest["components"]}:
                raise ContractError("Composant absent du manifeste : %s." % component_id)
            if component_id in self._handlers:
                raise ContractError("Composant déjà géré : %s." % component_id)
            self._handlers[component_id] = handler
            return handler
        return register

    def run(self, stdin=None, stdout=None, stderr=None):
        """Traite une requête. Retourne 0 si une réponse valide a été écrite, 1 pour un échec."""
        stdin = stdin or sys.stdin
        stdout = stdout or sys.stdout
        stderr = stderr or sys.stderr
        try:
            missing = [c["id"] for c in self.manifest["components"] if c["id"] not in self._handlers]
            if missing:
                raise ContractError("Composants sans gestionnaire : %s." % ", ".join(missing))
            line = stdin.readline(MAX_REQUEST_CHARACTERS + 1)
            if not line.strip():
                raise ContractError("Requête absente.")
            if len(line) > MAX_REQUEST_CHARACTERS:
                raise ContractError("Requête trop volumineuse.")
            request = json.loads(line)
            if not isinstance(request, dict) or request.get("contractVersion") != CONTRACT_VERSION:
                raise ContractError("Version du contrat non prise en charge.")
            kind = request.get("type")
            if kind == "describe":
                response = self.manifest
            elif kind == "execute":
                response = self._execute(request.get("invocation"))
            else:
                raise ContractError("Type de requête inconnu.")
            stdout.write(json.dumps(response, ensure_ascii=False) + "\n")
            stdout.flush()
            return 0
        except (InfrastructureError, ContractError, ValueError) as error:
            # Les messages de ces exceptions ne contiennent jamais de secret : les gestionnaires doivent le garantir.
            stderr.write("%s\n" % error)
            return 1
        except Exception as error:  # noqa: BLE001 - toute erreur inattendue est un échec d'infrastructure
            stderr.write("Erreur inattendue (%s).\n" % type(error).__name__)
            return 1

    def _execute(self, invocation):
        if not isinstance(invocation, dict):
            raise ContractError("Invocation absente.")
        reference = invocation.get("component") or {}
        if (invocation.get("contractVersion") != CONTRACT_VERSION
                or reference.get("pluginId") != self.manifest["id"]
                or reference.get("pluginVersion") != self.manifest["version"]):
            raise ContractError("Invocation destinée à un autre plugin ou à une autre version.")
        component = next((c for c in self.manifest["components"]
                          if c["id"] == reference.get("componentId")
                          and c["version"] == reference.get("componentVersion")
                          and c["configurationSchema"]["version"] == reference.get("configurationSchemaVersion")), None)
        if component is None:
            raise ContractError("Composant inconnu de ce plugin.")
        outcome = self._handlers[component["id"]](Context(invocation))
        if not isinstance(outcome, Outcome) or (outcome.output_id is None) == (outcome.terminal is None):
            raise ContractError("Un composant doit retourner exactement une sortie ou un résultat terminal.")
        if outcome.output_id is not None and outcome.output_id not in {o["id"] for o in component["outputs"]}:
            raise ContractError("Sortie non déclarée par le composant : %s." % outcome.output_id)
        return {
            "contractVersion": CONTRACT_VERSION,
            "invocationId": invocation.get("invocationId"),
            "leaseEpoch": invocation.get("leaseEpoch"),
            "outputId": outcome.output_id,
            "terminalResult": outcome.terminal,
            "variableChanges": outcome.variables,
            "candidateArtifactId": None,
            "detail": outcome.detail,
        }


# --- correspondance de chemins (même format et mêmes règles que Encoding.Contracts LibraryPathMapping) -------------

def _normalize_remote(path):
    normalized = path.strip().replace("\\", "/")
    while "//" in normalized:
        normalized = normalized.replace("//", "/")
    return normalized.rstrip("/") if len(normalized) > 1 else normalized


def parse_mappings(text):
    """Parse « identifiant-bibliothèque=/chemin/vu/par/le/service », une ligne par bibliothèque.

    Retourne {identifiant: racine}. Lève ValueError si le format est invalide.
    """
    result = {}
    for raw in (text or "").split("\n"):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        separator = line.find("=")
        if separator <= 0:
            raise ValueError("Format de correspondance invalide.")
        library = str(uuid.UUID(line[:separator].strip()))
        root = _normalize_remote(line[separator + 1:])
        if not root or library in result or root in result.values():
            raise ValueError("Racine vide ou déclarée deux fois.")
        result[library] = root
    return result


def to_remote(mappings, library_id, relative_path):
    """Chemin d'un fichier de la bibliothèque tel que le service le voit, ou None si elle n'est pas associée."""
    root = mappings.get(str(uuid.UUID(str(library_id))))
    return None if root is None else root.rstrip("/") + "/" + relative_path.replace("\\", "/")


def remote_path(ctx):
    """(chemin du fichier traité vu par le service, message d'erreur métier) d'après la configuration du plugin."""
    if not ctx.library_id or not ctx.media_path:
        return None, "Le fichier traité n'est pas rattaché à une bibliothèque."
    try:
        mappings = parse_mappings(ctx.plugin_text("pathMappings"))
    except ValueError:
        return None, "La correspondance des chemins du plugin est invalide."
    path = to_remote(mappings, ctx.library_id, ctx.media_path)
    if path is None:
        return None, "Cette bibliothèque n'a pas de correspondance de chemin dans la configuration du plugin."
    return path, None


# --- client HTTP JSON ---------------------------------------------------------------------------------------------

class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class JsonApi:
    """Client HTTP JSON pour un service externe.

    Pas de redirection, délai et taille de réponse bornés. 404 est rendu à l'appelant ((404, None)) car c'est
    un résultat métier fréquent ; toute autre erreur est une InfrastructureError dont le message ne contient ni
    l'adresse complète ni les en-têtes (donc pas la clé).
    """

    MAX_RESPONSE_BYTES = 64 * 1024 * 1024
    TIMEOUT_SECONDS = 30

    def __init__(self, name, base_url, headers, opener=None):
        parts = urllib.parse.urlsplit(base_url or "")
        if parts.scheme not in ("http", "https") or not parts.hostname or parts.username or parts.password:
            raise InfrastructureError("L'adresse de %s doit être une URL HTTP ou HTTPS sans identifiant intégré." % name)
        self.name = name
        self.base = base_url if base_url.endswith("/") else base_url + "/"
        self.headers = dict(headers)
        self.opener = opener or urllib.request.build_opener(_NoRedirect)

    def request(self, method, path, body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("Accept", "application/json")
        for key, value in self.headers.items():
            request.add_header(key, value)
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with self.opener.open(request, timeout=self.TIMEOUT_SECONDS) as response:
                payload = response.read(self.MAX_RESPONSE_BYTES + 1)
                if len(payload) > self.MAX_RESPONSE_BYTES:
                    raise InfrastructureError("La réponse de %s dépasse la limite." % self.name)
                return response.status, (json.loads(payload) if payload.strip() else None)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return 404, None
            if error.code in (401, 403):
                raise InfrastructureError("%s a refusé la clé API." % self.name)
            raise InfrastructureError("%s a répondu avec le statut HTTP %d." % (self.name, error.code))
        except (urllib.error.URLError, socket.timeout, ConnectionError):
            raise InfrastructureError("Impossible de joindre %s depuis ce worker." % self.name)
        except ValueError:
            raise InfrastructureError("%s a répondu avec un contenu illisible." % self.name)
