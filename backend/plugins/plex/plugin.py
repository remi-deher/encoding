#!/usr/bin/env python3
"""Plugin Plex : actualiser une bibliothèque, attendre la fin de son scan, vérifier qu'un élément est indexé.

Le fichier traité est retrouvé par la correspondance de chemins du plugin (bibliothèque Encoding -> chemin vu
par Plex) : la section Plex est celle dont un dossier est le plus long préfixe du fichier, et l'actualisation
est limitée au dossier du fichier. Sans correspondance configurée, les composants gardent leur comportement
global (toutes les sections). Jeton envoyé en en-tête X-Plex-Token, jamais dans l'URL.
"""
import datetime
import os
import sys
import time
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
for candidate in (HERE, os.path.join(HERE, "..", "sdk")):
    if os.path.isdir(candidate) and candidate not in sys.path:
        sys.path.insert(0, candidate)

from encoding_plugin_sdk import InfrastructureError, JsonApi, Outcome, Plugin, load_manifest, remote_path  # noqa: E402

POLL_SECONDS = 2
DEFAULT_WAIT_SECONDS = 120
MAX_WAIT_SECONDS = 600
SAFETY_MARGIN_SECONDS = 5
ITEM_TYPES = {"movie": 1, "show": 4}  # l'épisode est l'élément qui porte un fichier


def _norm(path):
    return path.replace("\\", "/")


class PlexClient:
    def __init__(self, base_url, token, opener=None):
        self.api = JsonApi("Plex", base_url, {"X-Plex-Token": token}, opener)

    def sections(self):
        """[(clé, type, [dossiers], en cours de scan)] de chaque section."""
        _, payload = self.api.request("GET", "library/sections")
        result = []
        for directory in ((payload or {}).get("MediaContainer") or {}).get("Directory") or []:
            if isinstance(directory, dict) and directory.get("key") is not None:
                locations = [loc["path"] for loc in directory.get("Location") or [] if isinstance(loc, dict) and isinstance(loc.get("path"), str)]
                refreshing = directory.get("refreshing") in (True, 1, "1", "true")
                result.append((str(directory["key"]), directory.get("type"), locations, refreshing))
        return result

    def refresh(self, section, folder=None):
        query = "" if folder is None else "?" + urllib.parse.urlencode({"path": folder})
        self.api.request("GET", "library/sections/%s/refresh%s" % (urllib.parse.quote(section, safe=""), query))

    def metadata_exists(self, rating_key):
        status, _ = self.api.request("GET", "library/metadata/%s" % urllib.parse.quote(rating_key, safe=""))
        return status != 404

    def playing_files(self):
        """Fichiers des sessions de lecture en cours (lectures en pause comprises) ; None dans la liste = session sans fichier connu."""
        _, payload = self.api.request("GET", "status/sessions")
        files = []
        for item in ((payload or {}).get("MediaContainer") or {}).get("Metadata") or []:
            parts = [part.get("file") for media in item.get("Media") or [] for part in media.get("Part") or [] if isinstance(part.get("file"), str)]
            files.extend(parts or [None])
        return files

    def has_file(self, section, kind, path):
        query = {"file": path, "X-Plex-Container-Start": 0, "X-Plex-Container-Size": 50}
        if kind in ITEM_TYPES:
            query["type"] = ITEM_TYPES[kind]
        _, payload = self.api.request("GET", "library/sections/%s/all?%s" % (urllib.parse.quote(section, safe=""), urllib.parse.urlencode(query)))
        for item in ((payload or {}).get("MediaContainer") or {}).get("Metadata") or []:
            for media in item.get("Media") or []:
                for part in media.get("Part") or []:
                    if isinstance(part.get("file"), str) and _norm(part["file"]) == _norm(path):
                        return True
        return False


def section_for(sections, path):
    """Section dont un dossier est le plus long préfixe du fichier (frontière de dossier respectée)."""
    target = _norm(path)
    best = None
    for section in sections:
        for location in section[2]:
            prefix = _norm(location).rstrip("/") + "/"
            if target.startswith(prefix) and (best is None or len(prefix) > best[0]):
                best = (len(prefix), section)
    return None if best is None else best[1]


def wait_seconds(ctx, now=None):
    """Durée d'attente permise : jusqu'à l'échéance de l'invocation (moins une marge), bornée."""
    try:
        deadline = datetime.datetime.fromisoformat(str(ctx.invocation.get("deadline")))
        remaining = (deadline - (now or datetime.datetime.now(datetime.timezone.utc))).total_seconds() - SAFETY_MARGIN_SECONDS
    except ValueError:
        return DEFAULT_WAIT_SECONDS
    return max(0, min(MAX_WAIT_SECONDS, remaining))


def build(opener=None, sleep=time.sleep, clock=time.monotonic):
    plugin = Plugin(load_manifest(__file__, "plex"))

    def client(ctx):
        base_url, token = ctx.plugin_text("baseUrl"), ctx.plugin_text("apiKey")
        if base_url is None:
            raise InfrastructureError("L'adresse de Plex n'est pas configurée.")
        if token is None:
            raise InfrastructureError("Le jeton de Plex n'est pas configuré.")
        return PlexClient(base_url, token, opener)

    def locate(ctx, api):
        """(section, chemin vu par Plex, erreur métier). Section None et pas d'erreur = toutes les sections."""
        explicit = ctx.number("sectionId")
        mapped = ctx.plugin_text("pathMappings") is not None
        if not mapped:
            return (None if explicit is None else str(int(explicit))), None, None
        path, error = remote_path(ctx)
        if error:
            return None, None, error
        if explicit is not None:
            return str(int(explicit)), path, None
        section = section_for(api.sections(), path)
        if section is None:
            return None, None, "Aucune bibliothèque Plex ne contient ce fichier."
        return section[0], path, None

    @plugin.component("plex-refresh-library")
    def refresh(ctx):
        api = client(ctx)
        section, path, error = locate(ctx, api)
        if error:
            return Outcome.output("error", error)
        if section is None:
            for key, _, _, _ in api.sections():
                api.refresh(key)
            return Outcome.output("success", "L'actualisation de toutes les bibliothèques a été demandée à Plex.")
        folder = None if path is None else path.rsplit("/", 1)[0]
        api.refresh(section, folder)
        return Outcome.output("success", "L'actualisation de la bibliothèque Plex %s a été demandée." % section)

    @plugin.component("plex-wait-library-scan")
    def wait(ctx):
        api = client(ctx)
        section, _, error = locate(ctx, api)
        if error:
            return Outcome.output("error", error)
        limit = wait_seconds(ctx)
        started = clock()
        while True:
            busy = [s for s in api.sections() if s[3] and (section is None or s[0] == section)]
            if not busy:
                return Outcome.output("success", "Plex a terminé le scan.")
            if clock() - started + POLL_SECONDS > limit:
                return Outcome.output("error", "Le scan Plex n'est pas terminé dans le délai disponible.")
            sleep(POLL_SECONDS)

    @plugin.component("plex-check-media-indexed")
    def check(ctx):
        api = client(ctx)
        rating_key = ctx.text("ratingKey")
        if rating_key is not None:
            return Outcome.output("found", "Élément connu de Plex.") if api.metadata_exists(rating_key) else Outcome.output("notFound", "Élément absent de Plex.")
        path, error = remote_path(ctx)
        if error:
            return Outcome.output("error", error)
        section = section_for(api.sections(), path)
        if section is None:
            return Outcome.output("notFound", "Aucune bibliothèque Plex ne contient ce fichier.")
        return Outcome.output("found", "Fichier connu de Plex.") if api.has_file(section[0], section[1], path) else Outcome.output("notFound", "Fichier absent de Plex.")

    @plugin.component("plex-check-playback")
    def playback(ctx):
        api = client(ctx)
        files = api.playing_files()
        if ctx.text("scope") == "global":
            return Outcome.output("playing", "Plex est en cours de lecture.") if files else Outcome.output("idle", "Aucune lecture en cours dans Plex.")
        path, error = remote_path(ctx)
        if error:
            return Outcome.output("error", error)
        if any(f is not None and _norm(f) == _norm(path) for f in files):
            return Outcome.output("playing", "Le fichier est en cours de lecture dans Plex.")
        return Outcome.output("idle", "Le fichier n'est pas en cours de lecture dans Plex.")

    return plugin


if __name__ == "__main__":
    sys.exit(build().run())
