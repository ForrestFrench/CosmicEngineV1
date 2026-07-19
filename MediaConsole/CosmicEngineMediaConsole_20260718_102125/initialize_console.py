#!/usr/bin/env python3
"""One-time migration from the latest reviewed catalog into persistent console data."""

from collections import defaultdict
from datetime import datetime
from pathlib import Path
import json
import os

HERE = Path(__file__).resolve().parent
DATA = HERE / "data"


def write(path: Path, value):
    temp = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    temp.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")
    os.replace(temp, path)


catalog_path = DATA / "ATOM_CATALOG.json"
review_path = DATA / "REVIEW_STATE.json"
catalog = json.loads(catalog_path.read_text())
review = json.loads(review_path.read_text())
now = datetime.now().astimezone().isoformat(timespec="seconds")
catalog["schema_version"] = "3.0-media-console"
catalog["package_id"] = "CosmicEngineMediaConsole_20260718_102125"
catalog["review_status_vocabulary"] = ["candidate", "needs_review", "approved", "rejected"]
write(catalog_path, catalog)

media_roots_path = DATA / "MEDIA_ROOTS.json"
media_roots = json.loads(media_roots_path.read_text())
media_roots.setdefault("roots", {})["console"] = str(HERE)
write(media_roots_path, media_roots)

by_source = defaultdict(list)
for atom in catalog["atoms"]:
    by_source[atom["source_full_path"]].append(atom["atom_id"])
sources = {}
for full_path, atom_ids in sorted(by_source.items()):
    path = Path(full_path)
    stat = path.stat() if path.is_file() else None
    relative = next(atom.get("source_relative_path", path.name) for atom in catalog["atoms"] if atom["atom_id"] == atom_ids[0])
    sources[str(path.resolve())] = {
        "source_full_path": str(path.resolve()), "source_relative_path": relative, "filename": path.name,
        "file_size_bytes": stat.st_size if stat else None, "modified_ns": stat.st_mtime_ns if stat else None,
        "status": "processed", "processed_at": now, "atom_ids": atom_ids,
    }
inventory = {"schema_version": "1.0.0", "source_root": catalog.get("source_root"), "last_scan_at": None, "sources": sources}
write(DATA / "SOURCE_INVENTORY.json", inventory)

approved = {atom_id for atom_id, entry in review["atoms"].items() if entry.get("status") == "approved"}
runtime_atoms = [atom for atom in catalog["atoms"] if atom["atom_id"] in approved]
runtime = {"schema_version": "1.0.0", "generated_at": now, "review_state_last_saved_at": review.get("last_saved_at"), "approved_count": len(runtime_atoms), "atoms": runtime_atoms}
write(DATA / "RUNTIME_LIBRARY.json", runtime)
write(DATA / "INGESTION_STATE.json", {"schema_version": "1.0.0", "last_scan_at": None, "last_generation_at": None, "last_library_save_at": now, "new_sources": [], "changed_sources": [], "failures": [], "last_job": None})
print(json.dumps({"catalog_atoms": len(catalog["atoms"]), "review_entries": len(review["atoms"]), "processed_sources": len(sources), "runtime_approved": len(runtime_atoms)}, indent=2))
