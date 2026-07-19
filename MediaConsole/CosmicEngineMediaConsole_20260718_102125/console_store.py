#!/usr/bin/env python3
"""Persistent JSON data layers for the Cosmic Engine Media Console."""

from __future__ import annotations

from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional
import json
import os
import re
import shutil
import threading

VALID_STATUSES = {"candidate", "needs_review", "approved", "rejected"}

DEFAULT_EFFECTS = {
    "brightness": 1.0, "contrast": 1.0, "saturation": 1.0, "hue": 0,
    "grayscale": 0.0, "invert": 0.0, "posterize": False, "duotone": False,
    "speed": 1.0, "echo": 0.0, "trailSmear": 0.0, "datamosh": 0.0, "mirrorH": False, "mirrorV": False,
    "mirrorOverlay": False, "kaleidoscope": False, "zoom": 1.0, "rotation": 0, "distortion": 0.0,
    "grain": 0.0, "vignette": 0.0, "vhs": 0.0, "chromatic": 0.0, "blur": 0.0,
    "liquidIntensity": 0.0, "liquidScale": 0.35, "liquidSpeed": 0.2,
    "liquidDrift": 0.0, "liquidTurbulence": 0.35,
    "edgeSensitivity": 0.35, "edgeGlow": 0.0, "edgeWidth": 2.0,
    "sourceOpacity": 1.0, "edgeHue": 285.0, "hueCycle": False,
}
RETIRED_PRESETS = {"clean cinema", "liquid reverie", "liquid melt", "edge halo", "neon contour", "stutter oracle", "oracle stutter"}
DEFAULT_AUDIO_TUNING = {
    "enabled": True,
    "active_preset": "Gentle Instrument",
    "effect_mappings": {
        "liquid_warp": {"enabled": True, "source": "sustain", "sensitivity": 1.0, "response_speed": 0.35, "smoothing": 0.55, "max_contribution": 0.22, "decay_time": 1.8, "dead_zone": 0.07},
        "edge_glow": {"enabled": True, "source": "attack", "sensitivity": 1.0, "response_speed": 0.06, "smoothing": 0.18, "max_contribution": 0.18, "decay_time": 0.55, "dead_zone": 0.08},
        "mirror": {"enabled": True, "source": "combined_energy", "sensitivity": 1.0, "response_speed": 0.45, "smoothing": 0.62, "max_contribution": 0.20, "decay_time": 2.0, "dead_zone": 0.10},
        "color_saturation": {"enabled": True, "source": "combined_energy", "sensitivity": 1.0, "response_speed": 0.22, "smoothing": 0.48, "max_contribution": 0.16, "decay_time": 1.2, "dead_zone": 0.06},
    },
}
AUDIO_MAPPING_LEGACY = {"liquid_warp": "sustain", "edge_glow": "attack", "mirror": "interaction", "color_saturation": "intensity"}
AUDIO_MAPPING_SOURCES = {"guitar_a", "guitar_b", "combined_energy", "attack", "sustain"}


def now_iso() -> str:
    return datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds")


def atomic_json_write(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    with temp.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, path)


def clean_collection(value: str) -> str:
    return re.sub(r"\s+", " ", str(value or "").strip())[:80]


class ConsoleStore:
    def __init__(self, root: Path, source_root: Optional[Path] = None):
        self.root = root.resolve()
        self.data_dir = self.root / "data"
        self.backup_dir = self.root / "backups"
        self.catalog_path = self.data_dir / "ATOM_CATALOG.json"
        self.review_path = self.data_dir / "REVIEW_STATE.json"
        self.collections_path = self.data_dir / "COLLECTIONS.json"
        self.inventory_path = self.data_dir / "SOURCE_INVENTORY.json"
        self.runtime_path = self.data_dir / "RUNTIME_LIBRARY.json"
        self.ingestion_path = self.data_dir / "INGESTION_STATE.json"
        self.presets_path = self.data_dir / "EFFECT_PRESETS.json"
        self.exploration_path = self.data_dir / "EXPLORATION_STATE.json"
        self.media_roots_path = self.data_dir / "MEDIA_ROOTS.json"
        self.audio_tuning_path = self.data_dir / "AUDIO_TUNING_STATE.json"
        self.audio_tuning_presets_path = self.data_dir / "AUDIO_TUNING_PRESETS.json"
        self.lock = threading.RLock()
        self.job_lock = threading.RLock()
        self.job = {"active": False, "phase": "idle", "progress": 0, "message": "Idle", "atoms_generated": 0, "failures": []}
        self._load(source_root)

    def _load(self, source_root: Optional[Path]) -> None:
        for path in [self.catalog_path, self.review_path, self.collections_path, self.inventory_path, self.runtime_path, self.ingestion_path, self.presets_path, self.exploration_path, self.media_roots_path, self.audio_tuning_path, self.audio_tuning_presets_path]:
            if not path.is_file():
                raise FileNotFoundError(f"Missing Media Console data file: {path}")
        self.catalog = json.loads(self.catalog_path.read_text())
        self.review = json.loads(self.review_path.read_text())
        self.collections = json.loads(self.collections_path.read_text())
        self.inventory = json.loads(self.inventory_path.read_text())
        self.runtime = json.loads(self.runtime_path.read_text())
        self.ingestion = json.loads(self.ingestion_path.read_text())
        self.presets = json.loads(self.presets_path.read_text())
        self.exploration = json.loads(self.exploration_path.read_text())
        self.audio_tuning = self.sanitize_audio_tuning(json.loads(self.audio_tuning_path.read_text()))
        self.audio_tuning_presets = json.loads(self.audio_tuning_presets_path.read_text())
        self.source_root = (source_root or Path(self.catalog.get("source_root") or self.inventory.get("source_root"))).resolve()
        roots = json.loads(self.media_roots_path.read_text()).get("roots", {})
        self.media_roots = {str(name): Path(value).resolve() for name, value in roots.items() if Path(value).resolve().is_dir()}
        self.media_roots["console"] = self.root
        self._reindex()
        self._migrate_effect_state()

    def _reindex(self) -> None:
        self.atoms = self.catalog.get("atoms", [])
        self.atoms_by_id = {atom["atom_id"]: atom for atom in self.atoms}
        if len(self.atoms_by_id) != len(self.atoms):
            raise ValueError("Duplicate atom IDs in ATOM_CATALOG.json")
        for atom in self.atoms:
            if atom["atom_id"] not in self.review.setdefault("atoms", {}):
                self.review["atoms"][atom["atom_id"]] = {
                    "status": "candidate", "assigned_collections": list(atom.get("suggested_collections", [])),
                    "notes": "", "last_modified": now_iso(),
                }
        self.runtime_atoms = self.runtime.get("atoms", [])
        self.runtime_by_id = {atom["atom_id"]: atom for atom in self.runtime_atoms}

    def _migrate_effect_state(self) -> None:
        changed = False
        clean_presets = []
        for preset in self.presets.get("presets", []):
            if preset.get("name", "").strip().lower() in RETIRED_PRESETS:
                changed = True
                continue
            effects = {key: preset.get("effects", {}).get(key, default) for key, default in DEFAULT_EFFECTS.items()}
            clean_presets.append({**preset, "effects": effects})
        self.presets["presets"] = clean_presets
        effects = {key: self.exploration.get("effects", {}).get(key, default) for key, default in DEFAULT_EFFECTS.items()}
        if effects != self.exploration.get("effects"):
            self.exploration["effects"] = effects
            changed = True
        if self.exploration.get("current_atom_id") not in self.runtime_by_id and self.runtime_atoms:
            self.exploration["current_atom_id"] = self.runtime_atoms[0]["atom_id"]
            changed = True
        if changed:
            atomic_json_write(self.presets_path, self.presets)
            atomic_json_write(self.exploration_path, self.exploration)

    def backup(self, path: Path, label: str) -> Optional[Path]:
        if not path.exists():
            return None
        self.backup_dir.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        destination = self.backup_dir / f"{path.stem}_{stamp}_{label}{path.suffix}"
        shutil.copy2(path, destination)
        return destination

    def counts(self) -> Dict[str, Any]:
        statuses = Counter(entry.get("status", "candidate") for entry in self.review.get("atoms", {}).values())
        return {"total": len(self.atoms), "candidate": statuses["candidate"], "needs_review": statuses["needs_review"], "approved": statuses["approved"], "rejected": statuses["rejected"], "runtime": len(self.runtime_atoms)}

    def summary(self) -> Dict[str, Any]:
        counts = self.counts()
        return {
            "ok": True, "package_id": self.catalog.get("package_id"), "counts": counts,
            "last_scan_at": self.ingestion.get("last_scan_at"), "last_generation_at": self.ingestion.get("last_generation_at"),
            "last_library_save_at": self.ingestion.get("last_library_save_at"),
            "new_sources": self.ingestion.get("new_sources", []), "changed_sources": self.ingestion.get("changed_sources", []),
            "failures": self.ingestion.get("failures", []), "job": self.job_snapshot(),
        }

    def job_snapshot(self) -> Dict[str, Any]:
        with self.job_lock:
            return json.loads(json.dumps(self.job))

    def set_job(self, **changes) -> None:
        with self.job_lock:
            self.job.update(changes)

    def persist_ingestion(self) -> None:
        atomic_json_write(self.ingestion_path, self.ingestion)

    def persist_catalog_review_inventory(self) -> None:
        with self.lock:
            self.catalog["atom_count"] = len(self.catalog.get("atoms", []))
            self.review["last_saved_at"] = now_iso()
            atomic_json_write(self.catalog_path, self.catalog)
            atomic_json_write(self.review_path, self.review)
            atomic_json_write(self.inventory_path, self.inventory)
            self._reindex()

    def combined_atom(self, atom: Dict[str, Any]) -> Dict[str, Any]:
        entry = self.review["atoms"][atom["atom_id"]]
        result = dict(atom)
        result.update({
            "status": entry.get("status", "candidate"), "assigned_collections": entry.get("assigned_collections", []),
            "review_notes": entry.get("notes", ""), "review_last_modified": entry.get("last_modified"),
            "preview_url": f"/media/preview/{atom['atom_id']}", "thumbnail_url": f"/media/thumbnail/{atom['atom_id']}",
        })
        return result

    def review_queue(self, statuses: Optional[Iterable[str]] = None, limit: int = 2000) -> Dict[str, Any]:
        allowed = set(statuses or ["candidate", "needs_review"])
        records = [self.combined_atom(atom) for atom in self.atoms if self.review["atoms"][atom["atom_id"]].get("status") in allowed]
        return {"ok": True, "count": len(records), "atoms": records[:max(1, min(limit, 5000))]}

    def update_review(self, atom_id: str, status: Optional[str] = None, collections: Optional[List[str]] = None, notes: Optional[str] = None) -> Dict[str, Any]:
        if atom_id not in self.atoms_by_id:
            raise KeyError(f"Unknown atom: {atom_id}")
        with self.lock:
            entry = self.review["atoms"][atom_id]
            if status is not None:
                if status not in VALID_STATUSES:
                    raise ValueError(f"Invalid status: {status}")
                entry["status"] = status
            if collections is not None:
                entry["assigned_collections"] = list(dict.fromkeys(filter(None, (clean_collection(value) for value in collections))))
            if notes is not None:
                entry["notes"] = str(notes)[:10000]
            entry["last_modified"] = now_iso()
            self.review["last_saved_at"] = now_iso()
            atomic_json_write(self.review_path, self.review)
            return {"ok": True, "entry": dict(entry), "counts": self.counts()}

    def save_runtime_library(self) -> Dict[str, Any]:
        with self.lock:
            approved = {atom_id for atom_id, entry in self.review["atoms"].items() if entry.get("status") == "approved"}
            atoms = [atom for atom in self.atoms if atom["atom_id"] in approved]
            self.backup(self.runtime_path, "before-library-save")
            self.runtime = {"schema_version": "1.0.0", "generated_at": now_iso(), "review_state_last_saved_at": self.review.get("last_saved_at"), "approved_count": len(atoms), "atoms": atoms}
            atomic_json_write(self.runtime_path, self.runtime)
            self.runtime_atoms = atoms
            self.runtime_by_id = {atom["atom_id"]: atom for atom in atoms}
            self.ingestion["last_library_save_at"] = self.runtime["generated_at"]
            self.persist_ingestion()
            if self.exploration.get("current_atom_id") not in self.runtime_by_id and atoms:
                self.exploration["current_atom_id"] = atoms[0]["atom_id"]
                atomic_json_write(self.exploration_path, self.exploration)
            return {"ok": True, "approved_count": len(atoms), "generated_at": self.runtime["generated_at"]}

    def reload_runtime_library(self) -> Dict[str, Any]:
        with self.lock:
            self.runtime = json.loads(self.runtime_path.read_text())
            self.runtime_atoms = self.runtime.get("atoms", [])
            self.runtime_by_id = {atom["atom_id"]: atom for atom in self.runtime_atoms}
            return {"ok": True, "approved_count": len(self.runtime_atoms), "generated_at": self.runtime.get("generated_at")}

    def weight_groups(self, atom: Dict[str, Any], collections: List[str]) -> List[str]:
        text = " ".join([*(str(x).lower() for x in collections), *(str(x).lower() for x in atom.get("content_tags", [])), str(atom.get("description", "")).lower()])
        groups = ["normal"]
        if any(term in text for term in ("transition material", "loopable", "texture", "visual bridge")):
            groups.append("transition")
        if any(term in text for term in ("abstract", "film texture", "surreal", "experimental", "microscopy", "graphic", "psychedelic")):
            groups.append("experimental")
        return groups

    def public_runtime_atom(self, atom: Dict[str, Any]) -> Dict[str, Any]:
        aid = atom["atom_id"]
        collections = self.review.get("atoms", {}).get(aid, {}).get("assigned_collections", [])
        return {
            "atom_id": aid, "source_full_path": atom.get("source_full_path"),
            "start_time": float(atom.get("start_seconds", 0)), "end_time": float(atom.get("end_seconds", atom.get("duration_seconds", 0))),
            "preview_path": f"/media/preview/{aid}", "preview_url": f"/media/preview/{aid}", "source_url": f"/media/source/{aid}",
            "thumbnail_url": f"/media/thumbnail/{aid}", "start_timestamp": atom.get("start_timestamp"), "end_timestamp": atom.get("end_timestamp"),
            "duration_seconds": atom.get("duration_seconds"), "tier": atom.get("tier"), "description": atom.get("description"),
            "primary_subject": atom.get("primary_subject"), "content_tags": atom.get("content_tags", []), "motif_category": atom.get("motif_category"),
            "mood": atom.get("mood"), "energy_level": atom.get("energy_level"), "motion_intensity": atom.get("motion_intensity"),
            "procedural_synergy_score": atom.get("procedural_synergy_score"), "collections": collections,
            "weight_groups": self.weight_groups(atom, collections),
        }

    def exploration_bootstrap(self) -> Dict[str, Any]:
        return {"schema_version": "3.1-audio-tuning", "approved_count": len(self.runtime_atoms), "atoms": [self.public_runtime_atom(atom) for atom in self.runtime_atoms], "presets": self.presets.get("presets", []), "session": self.exploration, "audio_tuning": self.audio_tuning, "audio_tuning_presets": self.audio_tuning_presets.get("presets", []), "weights": {"normal": .7, "transition": .2, "experimental": .1}}

    @staticmethod
    def sanitize_audio_tuning(payload: Dict[str, Any]) -> Dict[str, Any]:
        result = {"schema_version": "2.0.0", "last_modified": now_iso(), "enabled": bool(payload.get("enabled", True)), "active_preset": str(payload.get("active_preset") or "Custom")[:80], "effect_mappings": {}}
        incoming = payload.get("effect_mappings", {})
        legacy = payload.get("mappings", {})
        for name, defaults in DEFAULT_AUDIO_TUNING["effect_mappings"].items():
            candidate = incoming.get(name, {})
            if not isinstance(candidate, dict) or not candidate:
                candidate = legacy.get(AUDIO_MAPPING_LEGACY[name], {})
            values = candidate if isinstance(candidate, dict) else {}
            source = str(values.get("source", defaults["source"]))
            result["effect_mappings"][name] = {
                "enabled": bool(values.get("enabled", defaults["enabled"])),
                "source": source if source in AUDIO_MAPPING_SOURCES else defaults["source"],
                "sensitivity": max(0.1, min(4.0, float(values.get("sensitivity", defaults["sensitivity"])))),
                "response_speed": max(0.02, min(3.0, float(values.get("response_speed", defaults["response_speed"])))),
                "smoothing": max(0.0, min(0.95, float(values.get("smoothing", defaults["smoothing"])))),
                "max_contribution": max(0.0, min(1.0, float(values.get("max_contribution", defaults["max_contribution"])))),
                "decay_time": max(0.05, min(5.0, float(values.get("decay_time", defaults["decay_time"])))),
                "dead_zone": max(0.0, min(0.5, float(values.get("dead_zone", defaults["dead_zone"])))),
            }
        return result

    def save_audio_tuning(self, payload: Dict[str, Any]) -> Dict[str, Any]:
        with self.lock:
            self.audio_tuning = self.sanitize_audio_tuning(payload)
            atomic_json_write(self.audio_tuning_path, self.audio_tuning)
            return dict(self.audio_tuning)

    def save_audio_tuning_preset(self, name: str, settings: Dict[str, Any]) -> Dict[str, Any]:
        name = re.sub(r"\s+", " ", str(name or "").strip())[:80]
        if not name:
            raise ValueError("Audio tuning preset name is required")
        sanitized = self.sanitize_audio_tuning({**settings, "active_preset": name})
        record = {"name": name, "description": "Saved from Audio Reactive Tuning Mode.", "settings": sanitized}
        with self.lock:
            presets = self.audio_tuning_presets.setdefault("presets", [])
            index = next((i for i, preset in enumerate(presets) if preset.get("name", "").lower() == name.lower()), None)
            if index is None:
                presets.append(record)
            else:
                presets[index] = record
            self.audio_tuning_presets["last_modified"] = now_iso()
            atomic_json_write(self.audio_tuning_presets_path, self.audio_tuning_presets)
            self.audio_tuning = sanitized
            atomic_json_write(self.audio_tuning_path, self.audio_tuning)
            return {"settings": dict(self.audio_tuning), "presets": list(presets)}

    def save_exploration_session(self, payload: Dict[str, Any]) -> Dict[str, Any]:
        with self.lock:
            atom_id = payload.get("current_atom_id")
            if atom_id not in self.runtime_by_id:
                atom_id = self.exploration.get("current_atom_id")
            if atom_id not in self.runtime_by_id and self.runtime_atoms:
                atom_id = self.runtime_atoms[0]["atom_id"]
            duration_min = max(5, min(300, int(payload.get("effect_duration_min", 15))))
            duration_max = max(duration_min, min(300, int(payload.get("effect_duration_max", 45))))
            morph_min = max(1, min(60, int(payload.get("morph_duration_min", 5))))
            morph_max = max(morph_min, min(60, int(payload.get("morph_duration_max", 15))))
            self.exploration = {
                "schema_version": "3.0.0", "last_modified": now_iso(), "current_atom_id": atom_id,
                "current_preset": str(payload.get("current_preset") or "Custom")[:80],
                "effects": {key: payload.get("effects", {}).get(key, default) for key, default in DEFAULT_EFFECTS.items()},
                "effect_timeline_paused": bool(payload.get("effect_timeline_paused", False)), "effect_locked": bool(payload.get("effect_locked", False)),
                "effect_duration_min": duration_min, "effect_duration_max": duration_max, "morph_duration_min": morph_min, "morph_duration_max": morph_max,
                "deterministic_seed": int(payload.get("deterministic_seed", 1337)), "playback_quality": "original" if payload.get("playback_quality") == "original" else "proxy",
            }
            atomic_json_write(self.exploration_path, self.exploration)
            return dict(self.exploration)

    def save_preset(self, name: str, effects: Dict[str, Any], description: str = "User-created preset.") -> Dict[str, Any]:
        name = re.sub(r"\s+", " ", str(name or "").strip())[:80]
        if not name or name.lower() in RETIRED_PRESETS:
            raise ValueError("Preset name is empty or retired")
        record = {"name": name, "description": str(description or "")[:300], "effects": {key: effects.get(key, default) for key, default in DEFAULT_EFFECTS.items()}}
        with self.lock:
            index = next((i for i, preset in enumerate(self.presets.get("presets", [])) if preset.get("name", "").lower() == name.lower()), None)
            if index is None:
                self.presets.setdefault("presets", []).append(record)
            else:
                self.presets["presets"][index] = record
            self.presets["last_modified"] = now_iso()
            atomic_json_write(self.presets_path, self.presets)
            return record

    def media_path(self, kind: str, atom_id: str) -> Optional[Path]:
        atom = self.atoms_by_id.get(atom_id)
        if not atom:
            return None
        if kind == "source":
            path = Path(atom.get("source_full_path") or "").resolve()
            try:
                path.relative_to(self.source_root)
            except ValueError:
                return None
            return path if path.is_file() else None
        field = "preview_clip" if kind == "preview" else "representative_thumbnail"
        relative = atom.get(field)
        root = self.media_roots.get(str(atom.get("media_source", "console")))
        if not relative or not root:
            return None
        path = (root / relative).resolve()
        try:
            path.relative_to(root)
        except ValueError:
            return None
        return path if path.is_file() else None
