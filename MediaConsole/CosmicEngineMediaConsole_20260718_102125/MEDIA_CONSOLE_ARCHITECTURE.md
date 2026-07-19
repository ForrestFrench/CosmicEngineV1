# Cosmic Engine Media Console Architecture

## Purpose

The Media Console is the permanent, local video-tooling surface for Cosmic Engine. It unifies incremental atom ingestion, candidate review, approved-library publishing, and visual-effects exploration without merging their data responsibilities.

The console does not modify original source media, introduce a database, or integrate playback into the runtime engine.

## Data boundaries

| Layer | File | Responsibility |
|---|---|---|
| Source inventory | `data/SOURCE_INVENTORY.json` | Exact full paths and processing history for known VisionBoard videos |
| Complete atom catalog | `data/ATOM_CATALOG.json` | Every historical and newly generated atom, regardless of review status |
| Review overlay | `data/REVIEW_STATE.json` | `candidate`, `needs_review`, `approved`, or `rejected`; collections and notes |
| Approved runtime snapshot | `data/RUNTIME_LIBRARY.json` | Approved-only atoms explicitly published by the user |
| Effects presets | `data/EFFECT_PRESETS.json` | Independent effect states; no atom approval data |
| Exploration session | `data/EXPLORATION_STATE.json` | Current clip/effect timeline settings and playback-quality choice |
| Ingestion status | `data/INGESTION_STATE.json` | Last scan, last generation, new/changed sources, progress results, failures |

Writes use temporary files followed by an atomic replace. Runtime-library publication also creates a timestamped backup in `backups/`.

## Workflow

1. **Scan for New Videos** recursively compares supported files in VisionBoard against exact normalized paths in the source inventory.
2. Known paths are not decoded again. A known path whose size or modification time changed is reported for human attention and is not silently reprocessed.
3. **Generate Atoms** runs a background batch for sources whose state is `new`. It uses sparse keyframe scene analysis plus interval coverage, then creates review proxies, thumbnails, and contact sheets under `generated_media/batches/`.
4. Generated atoms are appended to the complete catalog with `candidate` status. Existing catalog records and review entries remain unchanged.
5. The review surface updates only the review overlay.
6. **Save Approved Atoms to Library** rebuilds the approved-only runtime snapshot. Rejected, candidate, and needs-review atoms are excluded.
7. **Refresh Atom Library** reloads that runtime snapshot into Visual Exploration without restarting the server or resetting the continuing effect state.

## Playback architecture

Visual Exploration reads only `RUNTIME_LIBRARY.json`. Proxy mode uses generated previews; Original mode streams the original source file and enforces atom start/end timestamps in the browser. Clip and effect timelines remain independent. Effect presets are selected with a deterministic seed and numeric morph parameters interpolate continuously.

## Safety model

- VisionBoard paths are exposed read-only; the console has no source-media write, rename, move, trim, transcode-in-place, or delete operation.
- Generated review assets live beneath the Media Console package.
- Source-serving requests are rejected unless the resolved path is inside the configured VisionBoard root.
- Generated-media requests are rejected unless they resolve inside a registered media root.
- A failed source is recorded and the rest of the batch continues.
- Existing sources are never regenerated merely because the console is restarted.

## Deliberately deferred

Automatic semantic scene understanding, OCR, licensing lookup, audio reactivity, SQLite, Scene Director, runtime-engine integration, and automated acceptance remain outside this phase.

## Future AI review integration

An AI reviewer can be added as a producer of recommendations without becoming the authority for review state. It should read candidate atoms and their thumbnails/contact sheets, then write a separate suggestion layer keyed by stable atom ID: proposed description, tags, motifs, collections, boundary revisions, confidence, and visible-text or rights flags. The human review surface can display those suggestions alongside the candidate. Only an explicit human action should update `REVIEW_STATE.json`, and only **Save Approved Atoms to Library** should update the runtime snapshot.

This keeps automated interpretation replaceable and auditable while preserving stable IDs and user decisions.

## Replacement for one-off curation runs

Past curation packages captured a single scan, a single candidate set, and a dashboard tied to that set. The Media Console turns those artifacts into continuing state: the processed-source inventory survives restarts, new batches append to the same complete catalog, review decisions remain in one overlay, and the approved runtime snapshot can be refreshed in place. Timestamped batch folders remain useful as generated-media provenance, but a new dashboard no longer needs to be built for every ingestion pass.
