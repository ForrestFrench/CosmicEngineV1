# Validation Report

## End-to-end isolated test

A synthetic 38-second video was created in a temporary VisionBoard fixture. The entire console data directory was copied to that temporary environment before the test, so the real VisionBoard and production review state were not used for writes.

Results:

- First scan found exactly one unseen video; the second scan found zero.
- No existing source was rescanned.
- Three unique candidate atoms were generated with readable preview clips.
- One test atom was approved and one rejected.
- Publishing added only the approved atom to the temporary runtime library.
- Live reload returned the new approved count without a server restart.
- The rejected test atom never entered playback.
- All 1,158 pre-existing review entries were byte-for-byte equivalent as JSON values after the workflow.
- The synthetic source SHA-256 remained unchanged.
- Catalog, review, and runtime JSON files parsed successfully.

Machine-readable evidence is in `VALIDATION_RESULTS.json`.

## Browser validation

- Both tabs loaded through the unified shell.
- Ingestion displayed 1,158 total decisions: 282 approved and 876 rejected, with 282 atoms in the runtime library.
- Visual Exploration loaded 282 approved atoms.
- Original mode reported source dimensions during playback and respected timestamp-backed source loading.
- Refresh Atom Library completed in place and retained the 282-atom live set.
- A slow effect morph was observed progressing while the clip changed underneath it, confirming independent timelines.
- Screenshots capture the ingestion tab, effects tab, and a morph in progress.

## Known limitations

- New candidates receive dependable technical, color, motion-role, and procedural metadata, but semantic subject descriptions, OCR, and rights clearance still require human review.
- Sparse keyframe scene detection is designed for recall and efficiency; it is not automatic artistic scene selection.
- Original-mode performance depends on source codec, drive throughput, browser decoding support, and effect complexity.
- The local server is a single-user studio tool and has no authentication or network deployment hardening.
- The walkthrough recording is a visual interface sequence assembled from verified dashboard captures, not a narrated screen recording.
