#!/usr/bin/env python3
"""Incremental, review-only atom generation for sources unseen by the console."""

from __future__ import annotations

from datetime import datetime
from hashlib import sha1
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageStat
import json
import math
import re
import subprocess

from console_store import ConsoleStore, now_iso

VIDEO_EXTENSIONS = {".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm", ".mpg", ".mpeg"}
FFMPEG = "/Users/admin/.local/bin/ffmpeg"
DURATION = re.compile(r"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")
VIDEO = re.compile(r"Video: (?P<codec>[^ ,]+).*?, (?P<width>\d{2,5})x(?P<height>\d{2,5})(?:\s|\[|,)")
FPS = re.compile(r"(?:,|\s)(?P<fps>\d+(?:\.\d+)?) fps")
BITRATE = re.compile(r"bitrate: (\d+) kb/s")
PTS = re.compile(r"pts_time:([0-9.]+)")


def stamp(seconds: float) -> str:
    milliseconds = int(round(seconds * 1000))
    return f"{milliseconds // 3600000:02d}:{milliseconds % 3600000 // 60000:02d}:{milliseconds % 60000 // 1000:02d}.{milliseconds % 1000:03d}"


def probe(path: Path) -> dict:
    completed = subprocess.run([FFMPEG, "-hide_banner", "-i", str(path)], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True, timeout=40)
    text = completed.stderr
    duration_match = DURATION.search(text)
    video_line = next((line for line in text.splitlines() if "Video:" in line), "")
    video_match = VIDEO.search(video_line)
    if not duration_match or not video_match:
        raise ValueError("Could not parse video metadata")
    hours, minutes, seconds = int(duration_match.group(1)), int(duration_match.group(2)), float(duration_match.group(3))
    fps_match, bitrate_match = FPS.search(video_line), BITRATE.search(text)
    return {
        "duration_seconds": round(hours * 3600 + minutes * 60 + seconds, 3),
        "width": int(video_match.group("width")), "height": int(video_match.group("height")),
        "frame_rate": float(fps_match.group("fps")) if fps_match else None,
        "codec": video_match.group("codec"), "bit_rate": int(bitrate_match.group(1)) * 1000 if bitrate_match else None,
    }


class IngestionEngine:
    def __init__(self, store: ConsoleStore):
        self.store = store

    def scan(self) -> dict:
        root = self.store.source_root
        if not root.is_dir():
            raise FileNotFoundError(f"VisionBoard is unavailable: {root}")
        known = self.store.inventory.setdefault("sources", {})
        new_sources, changed_sources, failures = [], [], []
        paths = sorted(path for path in root.rglob("*") if path.is_file() and path.suffix.lower() in VIDEO_EXTENSIONS)
        for path in paths:
            resolved = str(path.resolve())
            stat = path.stat()
            previous = known.get(resolved)
            if previous:
                if previous.get("file_size_bytes") not in (None, stat.st_size) or previous.get("modified_ns") not in (None, stat.st_mtime_ns):
                    changed_sources.append({"filename": path.name, "source_full_path": resolved, "reason": "size or modification time changed; not queued automatically"})
                continue
            try:
                metadata = probe(path)
                new_sources.append({
                    "filename": path.name, "source_relative_path": str(path.relative_to(root)), "source_full_path": resolved,
                    "file_size_bytes": stat.st_size, "modified_ns": stat.st_mtime_ns, "status": "new", **metadata,
                })
            except Exception as error:
                failures.append({"filename": path.name, "source_full_path": resolved, "phase": "scan", "error": f"{type(error).__name__}: {error}"})
        scanned_at = now_iso()
        self.store.inventory["last_scan_at"] = scanned_at
        self.store.ingestion.update({"last_scan_at": scanned_at, "new_sources": new_sources, "changed_sources": changed_sources, "failures": failures})
        self.store.persist_ingestion()
        from console_store import atomic_json_write
        atomic_json_write(self.store.inventory_path, self.store.inventory)
        return {"ok": True, "scanned": len(paths), "new_count": len(new_sources), "changed_count": len(changed_sources), "new_sources": new_sources, "changed_sources": changed_sources, "failures": failures, "last_scan_at": scanned_at}

    def _scene_times(self, source: Path) -> list[float]:
        command = [FFMPEG, "-hide_banner", "-nostats", "-skip_frame", "nokey", "-i", str(source), "-an", "-vf", "scale=160:-2,select='gt(scene,0.16)',showinfo", "-fps_mode", "vfr", "-f", "null", "-"]
        completed = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True, timeout=1200)
        if completed.returncode:
            raise RuntimeError(f"scene detection failed ({completed.returncode})")
        return [round(float(match.group(1)), 3) for match in PTS.finditer(completed.stderr)]

    @staticmethod
    def _candidates(duration: float, scenes: list[float]) -> list[tuple[float, float, str]]:
        centers = [min(duration - .5, 1 + index * 18) for index in range(max(1, math.ceil(max(1, duration - 1) / 18)))]
        for scene in scenes:
            if .5 < scene < duration - .5 and all(abs(scene - center) >= 8 for center in centers):
                centers.append(scene)
        centers = sorted(centers)[:150]
        candidates = []
        for index, center in enumerate(centers):
            role = "hero" if index % 11 == 0 and duration >= 12 else "micro" if index % 4 == 0 else "supporting"
            target = 18 if role == "hero" else 5 if role == "micro" else 11
            target = min(target, max(2, duration - .25))
            start = max(.1, min(duration - target, center - target * .42))
            end = min(duration - .05, start + target)
            start = max(.1, end - target)
            if end - start >= 2:
                candidates.append((round(start, 3), round(end, 3), role))
        unique = {}
        for candidate in candidates:
            unique[(round(candidate[0], 1), round(candidate[1], 1))] = candidate
        return sorted(unique.values())

    @staticmethod
    def _image_metadata(path: Path):
        image = Image.open(path).convert("RGB").resize((64, 64))
        quantized = image.quantize(colors=3, method=Image.Quantize.MEDIANCUT).convert("RGB")
        colors = [f"#{red:02x}{green:02x}{blue:02x}" for _, (red, green, blue) in sorted(quantized.getcolors(4096), reverse=True)]
        gray = image.convert("L")
        stats = ImageStat.Stat(gray)
        luma, contrast = stats.mean[0] / 255, stats.stddev[0] / 128
        return colors, {"label": "low" if luma < .32 else "high" if luma > .68 else "medium", "sampled_luma_0_1": round(luma, 3)}, {"label": "low" if contrast < .28 else "high" if contrast > .58 else "medium", "sampled_contrast_0_1": round(contrast, 3)}

    def _asset(self, source: Path, start: float, end: float, preview: Path, thumb: Path) -> None:
        preview.parent.mkdir(parents=True, exist_ok=True)
        thumb.parent.mkdir(parents=True, exist_ok=True)
        duration = end - start
        subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-ss", f"{start:.3f}", "-i", str(source), "-t", f"{duration:.3f}", "-an", "-vf", "scale='min(640,iw)':-2,fps=24", "-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", str(preview)], check=True, timeout=300)
        subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-ss", f"{duration / 2:.3f}", "-i", str(preview), "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "3", "-y", str(thumb)], check=True, timeout=60)

    def _sheet(self, filename: str, atoms: list[dict], destination: Path) -> None:
        columns, cell_width, cell_height = 4, 360, 250
        rows = math.ceil(len(atoms) / columns)
        sheet = Image.new("RGB", (columns * cell_width, 52 + rows * cell_height), "#101014")
        draw, font = ImageDraw.Draw(sheet), ImageFont.load_default(size=15)
        draw.text((14, 14), f"{filename} — generated candidates", fill="white", font=font)
        for index, atom in enumerate(atoms):
            image = Image.open(self.store.root / atom["representative_thumbnail"]).convert("RGB")
            image.thumbnail((340, 180))
            x, y = (index % columns) * cell_width + 10, 52 + (index // columns) * cell_height
            sheet.paste(image, (x, y))
            draw.text((x, y + 184), atom["atom_id"], fill="#f6dd9c", font=font)
            draw.text((x, y + 205), f"{atom['start_timestamp']}–{atom['end_timestamp']} · {atom['duration_seconds']:.1f}s", fill="white", font=font)
        destination.parent.mkdir(parents=True, exist_ok=True)
        sheet.save(destination, quality=90)

    def _generate_source(self, source_info: dict, batch_id: str) -> list[dict]:
        source = Path(source_info["source_full_path"])
        scenes = self._scene_times(source)
        segments = self._candidates(float(source_info["duration_seconds"]), scenes)
        atoms, existing_ids = [], set(self.store.atoms_by_id)
        stem_tags = [term for term in re.split(r"[^a-z0-9]+", source.stem.lower()) if len(term) > 2][:5]
        for index, (start, end, role) in enumerate(segments):
            digest = sha1(f"{source_info['source_relative_path']}|{source_info['file_size_bytes']}|{start:.3f}|{end:.3f}|media-console-v1".encode()).hexdigest()[:14]
            atom_id = f"ce-mc-{digest}"
            if atom_id in existing_ids:
                continue
            tier = "Tier 1" if role == "hero" else "Tier 3" if role == "micro" else "Tier 2"
            relative_base = Path("generated_media") / "batches" / batch_id
            preview_rel, thumb_rel = relative_base / "previews" / f"{atom_id}.mp4", relative_base / "thumbnails" / f"{atom_id}.jpg"
            self._asset(source, start, end, self.store.root / preview_rel, self.store.root / thumb_rel)
            colors, brightness, contrast = self._image_metadata(self.store.root / thumb_rel)
            energy = round(.36 + (index % 5) * .09, 3)
            motion = round(.32 + (index % 4) * .11, 3)
            loop = round(max(.28, .76 - motion * .35), 3)
            collections = ["Transition Material" if role == "micro" else "Background Texture", "Hero Visual" if role == "hero" else "Transformation"]
            atom = {
                "atom_id": atom_id, "source_filename": source.name, "source_relative_path": source_info["source_relative_path"], "source_full_path": str(source),
                "start_timestamp": stamp(start), "end_timestamp": stamp(end), "start_seconds": start, "end_seconds": end, "duration_seconds": round(end - start, 3),
                "atom_role": role, "tier": tier, "description": f"Scene-derived passage from {source.stem}; semantic subject review is still required.",
                "primary_subject": f"unclassified visual content from {source.stem}", "content_tags": stem_tags + [role, "new ingestion"],
                "motif_category": "transformation", "motifs": ["transformation"], "mood": "unclassified; review required",
                "energy_level": energy, "motion_intensity": motion, "camera_movement": "scene-derived; review camera motion",
                "dominant_colors": colors, "brightness": brightness, "contrast": contrast, "loop_quality": loop,
                "loopability_assessment": "review dissolve or feedback seam", "procedural_synergy_score": round(.56 + motion * .28, 3),
                "quality_confidence_score": .62, "suggested_collections": collections,
                "suggested_procedural_enhancements": ["color grading", "liquid warp", "edge glow", "feedback trails"],
                "suggested_audio_reactive_mappings": ["bass -> scale or displacement", "midrange -> color phase", "treble -> edge accents"],
                "allowed_effects": ["color grading", "liquid warp", "edge glow", "feedback", "bloom"],
                "effects_to_avoid": ["rapid stutter", "heavy sharpening", "large opaque typography"],
                "visible_logos_text_watermarks_or_licensing_concerns": "No automated OCR or rights lookup; inspect and clear source rights before use.",
                "representative_thumbnail": str(thumb_rel), "preview_clip": str(preview_rel), "media_source": "console",
                "notes": "Generated by the persistent Media Console using sparse scene and coverage analysis; manual semantic and boundary review required.",
            }
            atoms.append(atom)
            existing_ids.add(atom_id)
        if atoms:
            sheet_rel = Path("generated_media") / "batches" / batch_id / "contact_sheets" / f"{sha1(source_info['source_relative_path'].encode()).hexdigest()[:12]}.jpg"
            self._sheet(source.name, atoms, self.store.root / sheet_rel)
            for atom in atoms:
                atom["contact_sheet"] = str(sheet_rel)
        return atoms

    def generate(self) -> None:
        if self.store.job_snapshot().get("active"):
            return
        pending = [source for source in self.store.ingestion.get("new_sources", []) if source.get("status") == "new"]
        self.store.set_job(active=True, phase="generate", progress=0, message="Preparing new sources", atoms_generated=0, failures=[])
        failures, total_atoms = [], 0
        batch_id = datetime.now().strftime("%Y%m%d_%H%M%S")
        try:
            if not pending:
                self.store.set_job(active=False, phase="complete", progress=100, message="No new videos are waiting", atoms_generated=0, failures=[])
                return
            for source_index, source_info in enumerate(pending, 1):
                self.store.set_job(message=f"Analyzing {source_info['filename']}", progress=round((source_index - 1) / len(pending) * 100, 1))
                try:
                    atoms = self._generate_source(source_info, batch_id)
                    with self.store.lock:
                        self.store.catalog["atoms"].extend(atoms)
                        for atom in atoms:
                            self.store.review["atoms"][atom["atom_id"]] = {"status": "candidate", "assigned_collections": list(atom.get("suggested_collections", [])), "notes": "", "last_modified": now_iso()}
                        resolved = str(Path(source_info["source_full_path"]).resolve())
                        self.store.inventory.setdefault("sources", {})[resolved] = {**source_info, "status": "processed", "processed_at": now_iso(), "atom_ids": [atom["atom_id"] for atom in atoms]}
                        source_info["status"] = "processed"
                        source_info["atoms_generated"] = len(atoms)
                        self.store.persist_catalog_review_inventory()
                    total_atoms += len(atoms)
                    self.store.set_job(atoms_generated=total_atoms)
                except Exception as error:
                    failure = {"filename": source_info["filename"], "phase": "generate", "error": f"{type(error).__name__}: {error}"}
                    failures.append(failure)
                    source_info["status"] = "failed"
                    self.store.inventory.setdefault("sources", {})[str(Path(source_info["source_full_path"]).resolve())] = {**source_info, "status": "failed", "error": failure["error"], "atom_ids": []}
            self.store.ingestion["last_generation_at"] = now_iso()
            self.store.ingestion["failures"] = failures
            self.store.ingestion["last_job"] = {"batch_id": batch_id, "atoms_generated": total_atoms, "failures": failures, "completed_at": now_iso()}
            self.store.persist_ingestion()
            from console_store import atomic_json_write
            atomic_json_write(self.store.inventory_path, self.store.inventory)
            self.store.set_job(active=False, phase="complete", progress=100, message=f"Ready for review: {total_atoms} atoms", atoms_generated=total_atoms, failures=failures)
        except Exception as error:
            self.store.set_job(active=False, phase="failed", message=f"Generation failed: {error}", failures=failures + [{"error": str(error)}])
