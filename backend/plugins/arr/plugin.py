#!/usr/bin/env python3
"""Plugins Sonarr et Radarr (un même programme, déclaré deux fois sur le worker).

Usage : plugin.py <sonarr|radarr>

Composants : relire les fichiers de la série/du film qui contient le fichier traité, lancer la recherche des
éléments manquants, vérifier que le fichier est connu du service. Le fichier traité est retrouvé par la
correspondance de chemins du plugin (bibliothèque Encoding -> chemin vu par le service). Le déclencheur d'import
(webhook) est côté serveur Encoding : voir docs/architecture/0029-integration-plugins-and-sdk.md.
"""
import json
import os
import socket
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
for candidate in (HERE, os.path.join(HERE, "..", "sdk")):
    if os.path.isdir(candidate) and candidate not in sys.path:
        sys.path.insert(0, candidate)

from encoding_plugin_sdk import InfrastructureError, Outcome, Plugin, load_manifest  # noqa: E402

MAX_RESPONSE_BYTES = 64 * 1024 * 1024
TIMEOUT_SECONDS = 30

FLAVORS = {
    "sonarr": {
        "name": "Sonarr", "api_versions": ("v5", "v3"), "collection": "series", "item_key": "seriesId",
        "refresh": "sonarr-refresh-series", "search": "sonarr-search-missing-episodes", "check": "sonarr-check-episode-indexed",
        "refresh_command": "RescanSeries", "search_command": "MissingEpisodeSearch", "id_list": False,
        "check_id_key": "episodeId", "check_id_path": "episode/%d",
    },
    "radarr": {
        "name": "Radarr", "api_versions": ("v3",), "collection": "movie", "item_key": "movieId",
        "refresh": "radarr-refresh-movie", "search": "radarr-search-movie", "check": "radarr-check-movie-indexed",
        "refresh_command": "RescanMovie", "search_command": "MoviesSearch", "id_list": True,
        "check_id_key": "movieId", "check_id_path": "movie/%d",
    },
}


# --- correspondance de chemins (même format et mêmes règles que Encoding.Contracts LibraryPathMapping) -------------

def _normalize_remote(path):
    normalized = path.strip().replace("\\", "/")
    while "//" in normalized:
        normalized = normalized.replace("//", "/")
    return normalized.rstrip("/") if len(normalized) > 1 else normalized


def parse_mappings(text):
    """Retourne {identifiant-bibliothèque: racine vue par le service}. Lève ValueError si le format est invalide."""
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
    root = mappings.get(str(uuid.UUID(str(library_id))))
    return None if root is None else root.rstrip("/") + "/" + relative_path.replace("\\", "/")


# --- client HTTP -------------------------------------------------------------------------------------------------

class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class ArrClient:
    """Client minimal de l'API Sonarr/Radarr. Aucune valeur secrète n'apparaît dans les messages d'erreur."""

    def __init__(self, flavor, base_url, api_key, opener=None):
        parts = urllib.parse.urlsplit(base_url or "")
        if parts.scheme not in ("http", "https") or not parts.hostname or parts.username or parts.password:
            raise InfrastructureError("L'adresse de %s doit être une URL HTTP ou HTTPS sans identifiant intégré." % flavor["name"])
        self.flavor = flavor
        self.base = base_url if base_url.endswith("/") else base_url + "/"
        self.api_key = api_key
        self.opener = opener or urllib.request.build_opener(_NoRedirect)
        self.api_root = None

    def _send(self, method, path, body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("X-Api-Key", self.api_key)
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        name = self.flavor["name"]
        try:
            with self.opener.open(request, timeout=TIMEOUT_SECONDS) as response:
                payload = response.read(MAX_RESPONSE_BYTES + 1)
                if len(payload) > MAX_RESPONSE_BYTES:
                    raise InfrastructureError("La réponse de %s dépasse la limite." % name)
                return response.status, (json.loads(payload) if payload.strip() else None)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return 404, None
            if error.code in (401, 403):
                raise InfrastructureError("%s a refusé la clé API." % name)
            raise InfrastructureError("%s a répondu avec le statut HTTP %d." % (name, error.code))
        except (urllib.error.URLError, socket.timeout, ConnectionError):
            raise InfrastructureError("Impossible de joindre %s depuis ce worker." % name)
        except ValueError:
            raise InfrastructureError("%s a répondu avec un contenu illisible." % name)

    def _root(self):
        if self.api_root is None:
            for version in self.flavor["api_versions"]:
                status, _ = self._send("GET", "api/%s/system/status" % version)
                if status != 404:
                    self.api_root = "api/%s/" % version
                    break
            else:
                raise InfrastructureError("L'API de %s est introuvable. Vérifiez l'adresse et la version du service." % self.flavor["name"])
        return self.api_root

    def get(self, path):
        return self._send("GET", self._root() + path)

    def command(self, name, item_id):
        key = self.flavor["item_key"]
        body = {"name": name, key + "s": [item_id]} if self.flavor["id_list"] else {"name": name, key: item_id}
        self._send("POST", self._root() + "command", body)

    def items(self):
        """(id, dossier, fichier connu pour un film ou None) de chaque série ou film."""
        _, payload = self.get(self.flavor["collection"])
        result = []
        for item in payload or []:
            if isinstance(item, dict) and isinstance(item.get("id"), int) and isinstance(item.get("path"), str):
                movie_file = item.get("movieFile")
                known = movie_file.get("path") if isinstance(movie_file, dict) and isinstance(movie_file.get("path"), str) else None
                result.append((item["id"], item["path"], known))
        return result

    def episode_file_paths(self, series_id):
        _, payload = self.get("episodefile?seriesId=%d" % series_id)
        return [f["path"] for f in payload or [] if isinstance(f, dict) and isinstance(f.get("path"), str)]


def _norm(path):
    return path.replace("\\", "/")


def best_match(items, remote_path):
    """L'élément dont le dossier est le plus long préfixe du fichier (frontière de dossier respectée)."""
    target = _norm(remote_path)
    matches = [item for item in items if target.startswith(_norm(item[1]).rstrip("/") + "/")]
    return max(matches, key=lambda item: len(item[1]), default=None)


# --- composants --------------------------------------------------------------------------------------------------

def build(flavor_id, opener=None):
    flavor = FLAVORS[flavor_id]
    plugin = Plugin(load_manifest(__file__, flavor_id))
    name = flavor["name"]

    def client(ctx):
        base_url = ctx.plugin_text("baseUrl")
        api_key = ctx.plugin_text("apiKey")
        if base_url is None:
            raise InfrastructureError("L'adresse de %s n'est pas configurée." % name)
        if api_key is None:
            raise InfrastructureError("La clé API de %s n'est pas configurée." % name)
        return ArrClient(flavor, base_url, api_key, opener)

    def remote_path(ctx):
        """(chemin vu par le service, message d'erreur métier)."""
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

    def resolve_item(ctx, api):
        explicit = ctx.number(flavor["item_key"])
        if explicit is not None:
            return int(explicit), None
        path, error = remote_path(ctx)
        if error:
            return None, error
        match = best_match(api.items(), path)
        if match is None:
            return None, "Aucun élément %s ne contient ce fichier." % name
        return match[0], None

    def command_component(command, done):
        def handler(ctx):
            api = client(ctx)
            item_id, error = resolve_item(ctx, api)
            if error:
                return Outcome.output("error", error)
            api.command(command, item_id)
            return Outcome.output("success", "%s %s (identifiant %d)." % (done, name, item_id))
        return handler

    plugin.component(flavor["refresh"])(command_component(flavor["refresh_command"], "La relecture a été demandée à"))
    plugin.component(flavor["search"])(command_component(flavor["search_command"], "La recherche a été demandée à"))

    @plugin.component(flavor["check"])
    def check(ctx):
        api = client(ctx)
        explicit = ctx.number(flavor["check_id_key"])
        if explicit is not None:
            status, _ = api.get(flavor["check_id_path"] % int(explicit))
            return Outcome.output("notFound", "Élément absent du service.") if status == 404 else Outcome.output("found", "Élément connu du service.")
        path, error = remote_path(ctx)
        if error:
            return Outcome.output("error", error)
        items = api.items()
        if flavor_id == "radarr":
            known = any(item[2] is not None and _norm(item[2]) == _norm(path) for item in items)
        else:
            series = best_match(items, path)
            known = series is not None and any(_norm(p) == _norm(path) for p in api.episode_file_paths(series[0]))
        return Outcome.output("found", "Fichier connu de %s." % name) if known else Outcome.output("notFound", "Fichier absent de %s." % name)

    return plugin


def main(argv):
    if len(argv) != 2 or argv[1] not in FLAVORS:
        sys.stderr.write("Argument attendu : sonarr ou radarr.\n")
        return 2
    return build(argv[1]).run()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
