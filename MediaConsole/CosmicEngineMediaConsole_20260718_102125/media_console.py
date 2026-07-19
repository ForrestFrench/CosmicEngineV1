#!/usr/bin/env python3
"""Unified Cosmic Engine Media Console server."""

from __future__ import annotations

import argparse
import json
import mimetypes
import re
import threading
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, unquote, urlparse
from urllib.request import Request, urlopen

from console_store import ConsoleStore
from ingestion_engine import IngestionEngine


class MediaConsoleServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, handler, root: Path, source_root: Path, audio_core_url: str):
        super().__init__(address, handler)
        self.root = root
        self.audio_core_url = audio_core_url.rstrip("/")
        self.store = ConsoleStore(root, source_root=source_root)
        self.engine = IngestionEngine(self.store)
        self.worker: threading.Thread | None = None

    def audio_snapshot(self) -> dict:
        """Proxy intent data only; this process never captures or analyzes audio."""
        neutral = {
            "schema_version": 1,
            "sequence": 0,
            "captured_at_monotonic_ms": 0,
            "capture_active": False,
            "guitar_a": {"input_level": 0, "rms_energy": 0, "intensity": 0, "bass": 0, "mid": 0, "treble": 0, "attack": 0, "sustain": 0, "silent": True},
            "guitar_b": {"input_level": 0, "rms_energy": 0, "intensity": 0, "bass": 0, "mid": 0, "treble": 0, "attack": 0, "sustain": 0, "silent": True},
            "interaction": 0,
            "available": False,
        }
        try:
            request = Request(f"{self.audio_core_url}/audio/reactivity", headers={"Accept": "application/json"})
            with urlopen(request, timeout=0.20) as response:
                payload = json.loads(response.read(256 * 1024).decode("utf-8"))
            if not isinstance(payload, dict) or payload.get("schema_version") != 1:
                return neutral
            payload["available"] = True
            return payload
        except (HTTPError, URLError, TimeoutError, ValueError, json.JSONDecodeError, OSError):
            return neutral

    def audio_calibration_snapshot(self) -> dict:
        """Read the existing core calibration state; never calibrate or capture here."""
        try:
            request = Request(f"{self.audio_core_url}/calibration/status", headers={"Accept": "application/json"})
            with urlopen(request, timeout=0.20) as response:
                payload = json.loads(response.read(256 * 1024).decode("utf-8"))
            if not isinstance(payload, dict):
                raise ValueError("Invalid calibration response")
            payload["available"] = True
            return payload
        except (HTTPError, URLError, TimeoutError, ValueError, json.JSONDecodeError, OSError):
            return {"available": False, "audioCapturing": False, "inputA": {"gain": 1.0}, "inputB": {"gain": 1.0}}

    def set_audio_calibration(self, payload: dict) -> dict:
        """Forward bounded controls to the existing calibration layer."""
        input_name = str(payload.get("input", "A")).upper()
        field = str(payload.get("field", ""))
        if input_name not in {"A", "B"}:
            raise ValueError("Calibration input must be A or B")
        if field != "gain":
            raise ValueError("Only existing visual-control gain is exposed here")
        try:
            value = max(0.0, min(32.0, float(payload.get("value", 1.0))))
            body = json.dumps({"input": input_name, "field": field, "value": value}).encode("utf-8")
            request = Request(
                f"{self.audio_core_url}/calibration/manual",
                data=body,
                method="POST",
                headers={"Accept": "application/json", "Content-Type": "application/json"},
            )
            with urlopen(request, timeout=0.35) as response:
                response.read(256 * 1024)
        except (HTTPError, URLError, TimeoutError, OSError) as exc:
            raise ValueError("Audio core is offline; calibration gain was not changed") from exc
        return MediaConsoleServer.audio_calibration_snapshot(self)

    def start_generation(self) -> tuple[bool, str]:
        if self.worker and self.worker.is_alive():
            return False, "Atom generation is already running."

        def work():
            try:
                self.engine.generate()
            except Exception as exc:  # failure is also recorded in the job state
                self.store.set_job(active=False, phase="failed", message=f"Generation failed: {exc}", failures=[{"error": str(exc)}])

        self.worker = threading.Thread(target=work, daemon=True, name="atom-ingestion")
        self.worker.start()
        return True, "Atom generation started."


class Handler(BaseHTTPRequestHandler):
    server: MediaConsoleServer
    protocol_version = "HTTP/1.1"

    def handle(self):
        try:
            super().handle()
        except (BrokenPipeError, ConnectionResetError):
            pass

    def log_message(self, fmt, *args):
        if getattr(self.server, "quiet", False):
            return
        super().log_message(fmt, *args)

    def _json(self, payload, status=200):
        body = json.dumps(payload, indent=2, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _read_json(self):
        try:
            length = int(self.headers.get("Content-Length", "0"))
            return json.loads(self.rfile.read(length) or b"{}")
        except (ValueError, json.JSONDecodeError):
            raise ValueError("Request body must be valid JSON.")

    def _static(self, relative: str):
        aliases = {
            "": "index.html",
            "index.html": "index.html",
            "ingestion.html": "ingestion.html",
            "ingestion.js": "ingestion.js",
            "console.css": "console.css",
            "exploration.html": "exploration.html",
            "exploration.js": "exploration.js",
            "exploration.css": "exploration.css",
        }
        target_name = aliases.get(relative)
        if not target_name:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        target = self.server.root / "static" / target_name
        if not target.is_file():
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        data = target.read_bytes()
        mime = mimetypes.guess_type(target.name)[0] or "application/octet-stream"
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", f"{mime}; charset=utf-8" if mime.startswith("text/") or mime.endswith("javascript") else mime)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        self.wfile.write(data)

    def _media(self, kind: str, atom_id: str):
        path = self.server.store.media_path(kind, atom_id)
        if not path or not path.is_file():
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        size = path.stat().st_size
        start, end = 0, size - 1
        range_header = self.headers.get("Range")
        if range_header:
            match = re.match(r"bytes=(\d*)-(\d*)", range_header)
            if not match:
                self.send_error(HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
                return
            left, right = match.groups()
            if left:
                start = int(left)
                end = int(right) if right else end
            elif right:
                start = max(0, size - int(right))
            end = min(end, size - 1)
            if start > end:
                self.send_error(HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
                return
        length = end - start + 1
        self.send_response(HTTPStatus.PARTIAL_CONTENT if range_header else HTTPStatus.OK)
        self.send_header("Content-Type", mimetypes.guess_type(path.name)[0] or "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(length))
        if range_header:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        try:
            with path.open("rb") as handle:
                handle.seek(start)
                remaining = length
                while remaining:
                    chunk = handle.read(min(1024 * 1024, remaining))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    remaining -= len(chunk)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_GET(self):
        parsed = urlparse(self.path)
        path = unquote(parsed.path)
        query = parse_qs(parsed.query)
        try:
            if path in ("/", "/index.html", "/ingestion.html", "/ingestion.js", "/console.css", "/exploration.html", "/exploration.js", "/exploration.css"):
                self._static(path.lstrip("/"))
            elif path == "/api/health":
                self._json({"ok": True, "service": "Cosmic Engine Media Console"})
            elif path == "/api/summary":
                self._json(self.server.store.summary())
            elif path == "/api/job":
                self._json(self.server.store.job_snapshot())
            elif path == "/api/review":
                statuses = query.get("status", ["candidate,needs_review"])[0].split(",")
                self._json(self.server.store.review_queue(statuses))
            elif path == "/api/exploration/bootstrap":
                self._json(self.server.store.exploration_bootstrap())
            elif path == "/api/audio/reactivity":
                self._json(self.server.audio_snapshot())
            elif path == "/api/audio/calibration":
                self._json(self.server.audio_calibration_snapshot())
            elif path.startswith("/media/"):
                parts = path.strip("/").split("/", 2)
                if len(parts) != 3 or parts[1] not in {"preview", "thumbnail", "source"}:
                    self.send_error(HTTPStatus.NOT_FOUND)
                else:
                    self._media(parts[1], parts[2])
            else:
                self.send_error(HTTPStatus.NOT_FOUND)
        except Exception as exc:
            self._json({"ok": False, "error": str(exc)}, 500)

    def do_POST(self):
        parsed = urlparse(self.path)
        path = unquote(parsed.path)
        try:
            body = self._read_json()
            if path == "/api/scan":
                self._json(self.server.engine.scan())
            elif path == "/api/generate":
                started, message = self.server.start_generation()
                self._json({"ok": started, "message": message}, 202 if started else 409)
            elif path == "/api/review/update":
                self._json(self.server.store.update_review(
                    body.get("atom_id", ""), body.get("status"), body.get("collections"), body.get("notes")
                ))
            elif path == "/api/library/save":
                self._json(self.server.store.save_runtime_library())
            elif path == "/api/library/refresh":
                self._json(self.server.store.reload_runtime_library())
            elif path == "/api/exploration/session":
                self._json(self.server.store.save_exploration_session(body))
            elif path == "/api/audio/tuning":
                self._json(self.server.store.save_audio_tuning(body))
            elif path == "/api/audio/tuning/field":
                self._json(self.server.store.patch_audio_tuning_field(
                    body.get("mapping", ""), body.get("field", ""), body.get("value")
                ))
            elif path == "/api/audio/tuning/logo/field":
                self._json(self.server.store.patch_band_logo_field(
                    body.get("field", ""), body.get("value")
                ))
            elif path == "/api/audio/tuning/presets":
                self._json(self.server.store.save_audio_tuning_preset(body.get("name", ""), body.get("settings", {})))
            elif path == "/api/audio/calibration/manual":
                self._json(self.server.set_audio_calibration(body))
            elif path == "/api/exploration/presets":
                self.server.store.save_preset(body.get("name", ""), body.get("effects", {}), body.get("description", "User-created preset."))
                self._json({"ok": True, "presets": self.server.store.presets.get("presets", [])})
            else:
                self.send_error(HTTPStatus.NOT_FOUND)
        except ValueError as exc:
            self._json({"ok": False, "error": str(exc)}, 400)
        except Exception as exc:
            self._json({"ok": False, "error": str(exc)}, 500)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument("--source-root", type=Path, default=Path("/Volumes/External Hard Drive FCF/CosmicEngine/VisionBoard"))
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8140)
    parser.add_argument("--audio-core-url", default="http://localhost:8080")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    store = ConsoleStore(args.root.resolve(), source_root=args.source_root.resolve())
    if args.check:
        print(json.dumps(store.summary(), indent=2))
        return
    server = MediaConsoleServer(
        (args.host, args.port), Handler, args.root.resolve(), args.source_root.resolve(), args.audio_core_url
    )
    server.quiet = args.quiet
    print(f"Cosmic Engine Media Console: http://{args.host}:{args.port}", flush=True)
    print(f"Source media (read-only): {args.source_root.resolve()}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
