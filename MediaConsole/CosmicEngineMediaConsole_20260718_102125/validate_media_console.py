#!/usr/bin/env python3
"""Isolated end-to-end validation; never writes to the real VisionBoard or console data."""

from __future__ import annotations

from datetime import datetime
from hashlib import sha256
from pathlib import Path
import json
import shutil
import subprocess
import tempfile

from console_store import ConsoleStore
from ingestion_engine import FFMPEG, IngestionEngine, probe


PACKAGE = Path(__file__).resolve().parent


def digest(path: Path) -> str:
    value = sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            value.update(chunk)
    return value.hexdigest()


def main() -> None:
    started = datetime.now().astimezone().isoformat(timespec="seconds")
    with tempfile.TemporaryDirectory(prefix="cosmic-media-console-") as temporary:
        fixture_root = Path(temporary) / "console"
        source_root = Path(temporary) / "VisionBoard"
        source_root.mkdir(parents=True)
        for name in ["data", "static"]:
            shutil.copytree(PACKAGE / name, fixture_root / name)
        source = source_root / "isolated_console_validation.mp4"
        subprocess.run([
            FFMPEG, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "testsrc2=size=640x360:rate=24:duration=38", "-an", "-c:v", "libx264",
            "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-y", str(source),
        ], check=True, timeout=180)
        source_before = digest(source)
        store = ConsoleStore(fixture_root, source_root=source_root)
        engine = IngestionEngine(store)
        old_ids = set(store.atoms_by_id)
        old_review = json.loads(json.dumps(store.review["atoms"]))
        old_runtime_count = len(store.runtime_atoms)

        scan = engine.scan()
        assert scan["new_count"] == 1 and scan["changed_count"] == 0
        engine.generate()
        new_ids = sorted(set(store.atoms_by_id) - old_ids)
        assert len(new_ids) >= 2, new_ids
        assert all(store.review["atoms"][atom_id]["status"] == "candidate" for atom_id in new_ids)
        assert all(store.media_path("preview", atom_id) for atom_id in new_ids)
        assert all(probe(store.media_path("preview", atom_id))["duration_seconds"] > 1 for atom_id in new_ids)

        approved_id, rejected_id = new_ids[:2]
        store.update_review(approved_id, "approved", ["Validation Collection"], "Isolated approval test")
        store.update_review(rejected_id, "rejected", [], "Isolated rejection test")
        saved = store.save_runtime_library()
        refreshed = store.reload_runtime_library()
        bootstrap = store.exploration_bootstrap()
        runtime_ids = {atom["atom_id"] for atom in bootstrap["atoms"]}
        assert approved_id in runtime_ids
        assert rejected_id not in runtime_ids
        assert saved["approved_count"] == old_runtime_count + 1
        assert refreshed["approved_count"] == saved["approved_count"]
        assert engine.scan()["new_count"] == 0
        assert digest(source) == source_before
        assert all(store.review["atoms"][atom_id] == entry for atom_id, entry in old_review.items())
        assert len(store.atoms_by_id) == len(set(store.atoms_by_id))
        json.loads(store.catalog_path.read_text())
        json.loads(store.review_path.read_text())
        json.loads(store.runtime_path.read_text())

        results = {
            "ok": True, "started_at": started, "completed_at": datetime.now().astimezone().isoformat(timespec="seconds"),
            "isolation": "Temporary console copy and synthetic source outside VisionBoard",
            "fixture_source": {"duration_seconds": probe(source)["duration_seconds"], "resolution": "640x360", "sha256_unchanged": True},
            "scan": {"first_scan_new": 1, "second_scan_new": 0, "existing_sources_rescanned": 0},
            "generation": {"new_atoms": len(new_ids), "unique_ids": True, "preview_files_readable": True},
            "review": {"approved_test_atom": approved_id, "rejected_test_atom": rejected_id, "existing_decisions_unchanged": len(old_review)},
            "runtime": {"before": old_runtime_count, "after_publish": saved["approved_count"], "approved_present": True, "rejected_absent": True, "live_reload_count": refreshed["approved_count"]},
            "data_validation": {"catalog_json": True, "review_json": True, "runtime_json": True, "source_media_unchanged": True},
        }
    (PACKAGE / "VALIDATION_RESULTS.json").write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
