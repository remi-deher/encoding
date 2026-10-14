"""Tests des plugins Python : protocole du SDK, plugin d'exemple, plugins Sonarr/Radarr contre un faux service.

Exécution : python3 -m unittest discover -s plugins/tests -v   (depuis la racine du dépôt)
Chaque plugin est lancé comme processus réel, exactement comme le fait le worker.
"""
import io
import json
import os
import subprocess
import sys
import threading
import unittest
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGINS = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(PLUGINS, "sdk"))
sys.path.insert(0, os.path.join(PLUGINS, "arr"))

import encoding_plugin_sdk as sdk  # noqa: E402
import plugin as arr  # noqa: E402

ARR = os.path.join(PLUGINS, "arr", "plugin.py")
EXAMPLE = os.path.join(PLUGINS, "example", "plugin.py")
LIBRARY = str(uuid.uuid4())


def run_process(script, args, request):
    process = subprocess.run([sys.executable, "-B", script, *args], input=request, capture_output=True, text=True, timeout=60)
    return process.returncode, process.stdout, process.stderr


def invocation(manifest, component_id, config=None, plugin_config=None, library=LIBRARY, media_path=None, epoch=3):
    component = next(c for c in manifest["components"] if c["id"] == component_id)
    return {
        "contractVersion": 1, "invocationId": str(uuid.uuid4()), "executionId": str(uuid.uuid4()), "attempt": 1,
        "workerNodeId": str(uuid.uuid4()), "leaseEpoch": epoch, "nodeId": "node-1",
        "component": {"pluginId": manifest["id"], "pluginVersion": manifest["version"], "componentId": component["id"],
                      "componentVersion": component["version"], "configurationSchemaVersion": component["configurationSchema"]["version"]},
        "configuration": config or {},
        "context": {"currentArtifactId": "current.mkv", "variables": {}, "mediaPath": media_path, "libraryId": library},
        "deadline": "2099-01-01T00:00:00+00:00", "pluginConfiguration": plugin_config,
    }


def execute(script, args, inv):
    return run_process(script, args, json.dumps({"type": "execute", "contractVersion": 1, "invocation": inv}) + "\n")


class FakeArr:
    """Faux service Sonarr/Radarr : table de routage (méthode, chemin) -> (statut, JSON), requêtes enregistrées."""

    def __init__(self, routes):
        self.routes = routes
        self.requests = []
        fake = self

        class Handler(BaseHTTPRequestHandler):
            def _answer(self, method):
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length).decode() if length else ""
                fake.requests.append({"method": method, "path": self.path, "body": body, "key": self.headers.get("X-Api-Key")})
                status, payload = fake.routes.get((method, self.path), (404, None))
                data = b"" if payload is None else json.dumps(payload).encode()
                self.send_response(status)
                if status in (301, 302):
                    self.send_header("Location", "http://127.0.0.1:1/")
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):  # noqa: N802
                self._answer("GET")

            def do_POST(self):  # noqa: N802
                self._answer("POST")

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = "http://127.0.0.1:%d" % self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()

    def commands(self):
        return [json.loads(r["body"]) for r in self.requests if r["method"] == "POST"]


def sonarr_routes():
    return {
        ("GET", "/api/v5/system/status"): (404, None),
        ("GET", "/api/v3/system/status"): (200, {}),
        ("GET", "/api/v3/series"): (200, [{"id": 8, "path": "/tv/Show Extra"}, {"id": 7, "path": "/tv/Show"}]),
        ("GET", "/api/v3/episodefile?seriesId=7"): (200, [{"path": "/tv/Show/Season 1/e1.mkv"}]),
        ("GET", "/api/v3/episode/41"): (200, {}),
        ("GET", "/api/v3/episode/42"): (404, None),
        ("POST", "/api/v3/command"): (201, {}),
    }


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        with open(os.path.join(PLUGINS, "example", "manifest.json"), encoding="utf-8") as handle:
            self.manifest = json.load(handle)

    def test_describe_returns_the_manifest(self):
        code, out, _ = run_process(EXAMPLE, [], '{"type":"describe","contractVersion":1}\n')
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out), self.manifest)

    def test_requests_that_break_the_contract_fail_without_output(self):
        good = invocation(self.manifest, "example-tag-file", {"name": "episode"}, media_path="Shows/e1.mkv")
        bad_version = dict(good, component=dict(good["component"], pluginVersion="9.9.9"))
        bad_component = dict(good, component=dict(good["component"], componentId="inconnu"))
        for request in ('{"type":"describe","contractVersion":2}\n', '{"type":"nope","contractVersion":1}\n', "", "pas du json\n",
                        json.dumps({"type": "execute", "contractVersion": 1, "invocation": bad_version}) + "\n",
                        json.dumps({"type": "execute", "contractVersion": 1, "invocation": bad_component}) + "\n"):
            code, out, _ = run_process(EXAMPLE, [], request)
            self.assertEqual((code, out), (1, ""), request)

    def test_example_components_execute_as_a_process(self):
        code, out, err = execute(EXAMPLE, [], invocation(self.manifest, "example-tag-file", {"name": "episode"}, media_path="Shows/Show/e1.mkv", epoch=7))
        self.assertEqual(code, 0, err)
        result = json.loads(out)
        self.assertEqual((result["outputId"], result["leaseEpoch"], result["variableChanges"]), ("success", 7, {"episode": "e1.mkv"}))
        self.assertIsNone(result["terminalResult"])
        code, out, _ = execute(EXAMPLE, [], invocation(self.manifest, "example-name-matches", {"text": "SHOW"}, media_path="Shows/Show/e1.mkv"))
        self.assertEqual(json.loads(out)["outputId"], "yes")
        code, out, _ = execute(EXAMPLE, [], invocation(self.manifest, "example-name-matches", {"text": "film"}, media_path="Shows/Show/e1.mkv"))
        self.assertEqual(json.loads(out)["outputId"], "no")

    def test_sdk_rejects_undeclared_outputs_missing_handlers_and_double_results(self):
        def run(plugin, component_id):
            out, err = io.StringIO(), io.StringIO()
            inv = invocation(self.manifest, component_id, {"name": "x", "text": "x"}, media_path="a.mkv")
            code = plugin.run(io.StringIO(json.dumps({"type": "execute", "contractVersion": 1, "invocation": inv}) + "\n"), out, err)
            return code, out.getvalue()

        plugin = sdk.Plugin(self.manifest)
        plugin.component("example-tag-file")(lambda ctx: sdk.Outcome.output("inconnue"))
        plugin.component("example-name-matches")(lambda ctx: sdk.Outcome.output("yes"))
        self.assertEqual(run(plugin, "example-tag-file"), (1, ""), "sortie non déclarée")
        incomplete = sdk.Plugin(self.manifest)
        incomplete.component("example-tag-file")(lambda ctx: sdk.Outcome.output("success"))
        self.assertEqual(run(incomplete, "example-tag-file"), (1, ""), "composant du manifeste sans gestionnaire")
        both = sdk.Plugin(self.manifest)
        both.component("example-tag-file")(lambda ctx: sdk.Outcome(output_id="success", terminal="success"))
        both.component("example-name-matches")(lambda ctx: sdk.Outcome.output("yes"))
        self.assertEqual(run(both, "example-tag-file"), (1, ""), "sortie et résultat terminal simultanés")
        with self.assertRaises(sdk.ContractError):
            sdk.Plugin(self.manifest).component("absent")(lambda ctx: None)

    def test_infrastructure_errors_exit_without_output(self):
        plugin = sdk.Plugin(self.manifest)

        def explode(ctx):
            raise sdk.InfrastructureError("service injoignable")

        plugin.component("example-tag-file")(explode)
        plugin.component("example-name-matches")(lambda ctx: sdk.Outcome.output("yes"))
        out, err = io.StringIO(), io.StringIO()
        inv = invocation(self.manifest, "example-tag-file", {"name": "x"}, media_path="a.mkv")
        code = plugin.run(io.StringIO(json.dumps({"type": "execute", "contractVersion": 1, "invocation": inv}) + "\n"), out, err)
        self.assertEqual((code, out.getvalue(), err.getvalue().strip()), (1, "", "service injoignable"))


class PathMappingTests(unittest.TestCase):
    """Mêmes vecteurs que les tests C# de LibraryPathMapping : les deux implémentations doivent concorder."""

    def setUp(self):
        with open(os.path.join(HERE, "path-mapping-vectors.json"), encoding="utf-8") as handle:
            self.vectors = json.load(handle)
        self.libraries = self.vectors["libraries"]

    def fill(self, text):
        for name, value in self.libraries.items():
            text = text.replace("{" + name + "}", value)
        return text

    def test_parse_and_map_to_remote(self):
        mappings = arr.parse_mappings(self.fill(self.vectors["mappings"]))
        self.assertEqual(mappings, {self.libraries[name]: root for name, root in self.vectors["roots"].items()})
        for case in self.vectors["toRemote"]:
            self.assertEqual(arr.to_remote(mappings, self.libraries[case["library"]], case["relative"]), case["remote"])
        self.assertIsNone(arr.to_remote(mappings, str(uuid.uuid4()), "a.mkv"))

    def test_invalid_mappings_are_refused(self):
        for text in self.vectors["invalid"]:
            with self.assertRaises(ValueError, msg=text):
                arr.parse_mappings(self.fill(text))


class SonarrTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakeArr(sonarr_routes())
        self.addCleanup(self.fake.close)
        self.manifest = sdk.load_manifest(ARR, "sonarr")

    def plugin_config(self, **overrides):
        config = {"baseUrl": self.fake.url, "apiKey": "secret-api-key", "pathMappings": "%s=/tv" % LIBRARY}
        config.update(overrides)
        return config

    def run_component(self, component, config=None, media="Show/Season 1/e1.mkv", library=LIBRARY, plugin_config=None):
        inv = invocation(self.manifest, component, config, plugin_config or self.plugin_config(), library, media)
        code, out, err = execute(ARR, ["sonarr"], inv)
        self.assertEqual(code, 0, err)
        result = json.loads(out)
        self.assertEqual((result["invocationId"], result["leaseEpoch"], result["contractVersion"]), (inv["invocationId"], inv["leaseEpoch"], 1))
        return result

    def test_manifest_is_the_one_served_to_the_backend(self):
        code, out, _ = run_process(ARR, ["sonarr"], '{"type":"describe","contractVersion":1}\n')
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out), self.manifest)

    def test_refresh_rescans_the_series_containing_the_file(self):
        result = self.run_component("sonarr-refresh-series")
        self.assertEqual(result["outputId"], "success")
        self.assertEqual(self.fake.commands(), [{"name": "RescanSeries", "seriesId": 7}], "pas la série voisine au même préfixe")
        self.assertTrue(all(r["key"] == "secret-api-key" for r in self.fake.requests), "clé envoyée en en-tête")
        self.assertTrue(all("secret-api-key" not in r["path"] + r["body"] for r in self.fake.requests), "clé jamais dans l'URL ni le corps")

    def test_search_uses_explicit_id_or_the_file(self):
        self.run_component("sonarr-search-missing-episodes", {"seriesId": 9})
        self.assertEqual(self.fake.commands(), [{"name": "MissingEpisodeSearch", "seriesId": 9}])
        self.assertNotIn("/api/v3/series", [r["path"] for r in self.fake.requests], "pas de résolution de chemin avec un identifiant")
        self.run_component("sonarr-search-missing-episodes")
        self.assertEqual(self.fake.commands()[-1], {"name": "MissingEpisodeSearch", "seriesId": 7})

    def test_business_failures_take_the_error_output_without_a_command(self):
        unmapped = self.run_component("sonarr-refresh-series", library=str(uuid.uuid4()))
        self.assertEqual(unmapped["outputId"], "error")
        self.assertEqual(self.fake.requests, [], "pas d'appel sans correspondance de chemin")
        outside = self.run_component("sonarr-refresh-series", media="Autre/e1.mkv")
        self.assertEqual(outside["outputId"], "error")
        self.assertEqual(self.fake.commands(), [])
        invalid = self.run_component("sonarr-refresh-series", plugin_config=self.plugin_config(pathMappings="n'importe quoi"))
        self.assertEqual(invalid["outputId"], "error")

    def test_check_by_file_and_by_id(self):
        self.assertEqual(self.run_component("sonarr-check-episode-indexed")["outputId"], "found")
        self.assertEqual(self.run_component("sonarr-check-episode-indexed", media="Show/Season 1/e2.mkv")["outputId"], "notFound")
        self.assertEqual(self.run_component("sonarr-check-episode-indexed", media="Autre/e1.mkv")["outputId"], "notFound")
        self.assertEqual(self.run_component("sonarr-check-episode-indexed", {"episodeId": 41})["outputId"], "found")
        self.assertEqual(self.run_component("sonarr-check-episode-indexed", {"episodeId": 42})["outputId"], "notFound")

    def test_infrastructure_failures_stay_failures_and_never_leak_the_key(self):
        def failing(status):
            fake = FakeArr({("GET", "/api/v5/system/status"): (status, None), ("GET", "/api/v3/system/status"): (status, None)})
            self.addCleanup(fake.close)
            return fake

        for status in (401, 403, 500, 302):
            fake = failing(status)
            inv = invocation(self.manifest, "sonarr-refresh-series", None, self.plugin_config(baseUrl=fake.url), LIBRARY, "Show/e1.mkv")
            code, out, err = execute(ARR, ["sonarr"], inv)
            self.assertEqual((code, out), (1, ""), "HTTP %d" % status)
            self.assertNotIn("secret-api-key", err)
        for broken in ({"baseUrl": "http://127.0.0.1:1"}, {"baseUrl": "http://user:pass@127.0.0.1:1"}, {"baseUrl": "ftp://hote"}, {"baseUrl": None}, {"apiKey": None}):
            config = {k: v for k, v in self.plugin_config(**broken).items() if v is not None}
            code, out, err = execute(ARR, ["sonarr"], invocation(self.manifest, "sonarr-refresh-series", None, config, LIBRARY, "Show/e1.mkv"))
            self.assertEqual((code, out), (1, ""), broken)
            self.assertNotIn("secret-api-key", err)
            self.assertNotIn("pass", err)

    def test_the_program_needs_a_flavor(self):
        self.assertEqual(run_process(ARR, [], "")[0], 2)
        self.assertEqual(run_process(ARR, ["plex"], "")[0], 2)


class RadarrTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakeArr({
            ("GET", "/api/v3/system/status"): (200, {}),
            ("GET", "/api/v3/movie"): (200, [
                {"id": 3, "path": "/movies/Film (2020)", "movieFile": {"path": "/movies/Film (2020)/Film.mkv"}},
                {"id": 4, "path": "/movies/Autre"}]),
            ("POST", "/api/v3/command"): (201, {}),
        })
        self.addCleanup(self.fake.close)
        self.manifest = sdk.load_manifest(ARR, "radarr")

    def run_component(self, component, media):
        config = {"baseUrl": self.fake.url, "apiKey": "k", "pathMappings": "%s=/movies" % LIBRARY}
        code, out, err = execute(ARR, ["radarr"], invocation(self.manifest, component, None, config, LIBRARY, media))
        self.assertEqual(code, 0, err)
        return json.loads(out)

    def test_commands_use_id_lists(self):
        self.assertEqual(self.run_component("radarr-refresh-movie", "Film (2020)/Film.mkv")["outputId"], "success")
        self.assertEqual(self.fake.commands(), [{"name": "RescanMovie", "movieIds": [3]}])
        self.run_component("radarr-search-movie", "Film (2020)/Film.mkv")
        self.assertEqual(self.fake.commands()[-1], {"name": "MoviesSearch", "movieIds": [3]})

    def test_check_matches_the_exact_known_file(self):
        self.assertEqual(self.run_component("radarr-check-movie-indexed", "Film (2020)/Film.mkv")["outputId"], "found")
        self.assertEqual(self.run_component("radarr-check-movie-indexed", "Autre/Autre.mkv")["outputId"], "notFound")


if __name__ == "__main__":
    unittest.main()
