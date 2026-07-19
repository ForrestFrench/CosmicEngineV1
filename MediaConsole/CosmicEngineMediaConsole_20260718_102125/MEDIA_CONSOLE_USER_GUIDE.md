# Cosmic Engine Media Console User Guide

## Start the console

Keep the external drive mounted, then double-click `start_mediaConsole.command` (or the compatible uppercase `START_MEDIA_CONSOLE.command`). It starts the local console quietly in the background, opens `http://127.0.0.1:8140/`, and closes the temporary Terminal window automatically. If the console is already running, it simply opens the existing dashboard instead of starting a duplicate server.

## Tab 1 — Atom Ingestion & Library Management

Use the four controls from left to right:

1. **Scan for New Videos** compares VisionBoard with the persistent processed-source inventory. The source list shows only unseen videos. Changed known files are reported but not automatically reprocessed.
2. **Generate Atoms** creates candidate metadata and review media for queued sources. Progress and failures remain visible; long batches run without locking the dashboard.
3. **Review Candidates** opens the candidate decision area. Watch the short preview, check the timestamp and metadata, adjust collections or notes, then choose Approved, Rejected, Needs Review, or Keep Candidate.
4. **Save Approved Atoms to Library** deliberately publishes the current approved set. Review changes do not reach Visual Exploration until this button is used.

If generation is interrupted, scan again. Processed sources stay recorded and are skipped; failed or still-new entries remain visible for diagnosis.

## Tab 2 — Visual Exploration & Effects Review

This tab plays approved atoms only. Use **Refresh Atom Library** after publishing to load the new approved snapshot without restarting the console.

- **Proxy** is faster for browsing.
- **Original** streams the source video and uses the atom timestamps for high-quality effects review.
- Clip changes and effect morphs use independent timelines.
- Pause clips, pause effects, lock an effect state, skip a clip, or begin the next morph independently.
- Presets store effect parameters only; they never change atom approvals.

## Status meanings

- **Candidate** — newly generated and not yet decided.
- **Needs review** — deliberately deferred for closer inspection.
- **Approved** — eligible for the next published runtime-library snapshot.
- **Rejected** — preserved in the full catalog but excluded from playback.

## Troubleshooting

- If VisionBoard is unavailable, mount the external drive and scan again.
- If a source fails, read the issue shown in the first tab; other sources continue.
- If Visual Exploration does not show a newly approved atom, first save the approved library, then refresh it in the second tab.
- If original playback is heavy, switch to Proxy while browsing and return to Original for effect judgments.
