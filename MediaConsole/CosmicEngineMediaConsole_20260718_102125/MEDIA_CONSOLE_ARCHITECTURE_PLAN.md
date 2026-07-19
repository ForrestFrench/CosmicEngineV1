# Media Console architecture plan

## Reuse

- Keep the existing path-based processed-source comparison and FFmpeg/Pillow review-asset pipeline.
- Keep the approval dashboard's atomic JSON writes, status vocabulary, notes, and collection assignments.
- Keep the Effects Review v2.3 canvas, playback modes, presets, clip/effect timelines, morphing, and source-boundary behavior.

## Persistent data layers

1. `ATOM_CATALOG.json`: metadata for every discovered atom; no effect settings.
2. `REVIEW_STATE.json`: candidate/needs-review/approved/rejected decisions, notes, and collections.
3. `SOURCE_INVENTORY.json`: processed source identity, technical metadata, generated atom IDs, and failures.
4. `RUNTIME_LIBRARY.json`: replaceable approved-only snapshot consumed by Visual Exploration.
5. `EFFECT_PRESETS.json` and `EXPLORATION_STATE.json`: independent effect configuration.
6. `MEDIA_ROOTS.json`: validated read-only locations for existing review assets; future generated assets live under this console.

## Services and UI

- A single local HTTP server owns the state and media routes.
- Tab 1 scans, generates, reviews, and publishes metadata. Generation runs as a bounded background job with progress and per-source failures.
- Tab 2 embeds the retained Effects Review UI. `Refresh Atom Library` reloads the approved snapshot without restarting or resetting effects.
- Atomic writes and backups protect user decisions. Source files are always read-only.

## Ingestion lifecycle

`unseen source` → scan result → generation → `candidate` → human decision (`approved`, `rejected`, or `needs_review`) → Save To Library → approved-only runtime snapshot → Refresh Atom Library.

No database, source-media move, source transcode, audio reactivity, or Scene Director integration is introduced.
