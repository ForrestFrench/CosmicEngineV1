# ROADMAP.md

Immediate phases only — not a long-range plan. Update as phases complete or reorder.

**Last updated:** 2026-07-06 (Fable Strategy Review — Roadmap Pivot, see `AUDIT.md`)

## Pivot note

Prior order had "Stellar Nursery Visual Polish" as the immediate next phase after Live/Safe Profiles.
After three Stellar Nursery visual passes (one rejected, one accepted-but-flagged-as-too-soft, one
"should not be accepted yet") and an independent high-level strategy review (Fable), that order is
reversed: **infrastructure and hardware/audio validation now come before further visual polish.**
Rationale: the density debug view shows usable underlying structure, but the final composited frame
remains dark/soft/blurry — the likely bottleneck is the density-to-radiance/color/compositing transfer
stage, not more procedural density/noise detail. Continuing to add shader complexity at the density
stage is not expected to fix this efficiently. Stellar Nursery visual work is **parked, not abandoned**,
pending the transfer-function/hybrid-asset spike in P3.

## Phases

### P1 — RenderScale + Live/Safe Profiles + FPS Variance Evidence

- Add an adjustable internal render scale (affects actual render-target resolution, not just window/blit size).
- Add at least two named performance profiles: `Safe` and `High` (or `Art`).
- Log the active profile and the actual render target size in `[Perf]`.
- Gather FPS evidence across repeated bounded runs (not single runs) per profile.
- Use this pass to also investigate the open FPS-variance risk (see Known Risks below).
- **No shader-art changes in this pass.**
- Full acceptance criteria: see `PROJECT_STATE.md` "P1 Acceptance Criteria".

### P2 — OptiPlex + Real Guitar/Scarlett Validation

- Run the engine on the physical Dell OptiPlex 5070 Micro (the actual stage target — never yet tested).
- Capture FPS evidence per profile (Safe/High) on that hardware specifically.
- Confirm the actual audio interface identity in use (Scarlett 2i2 vs. Focusrite Clarett — docs currently disagree) and correct all docs to match.
- Confirm a real guitar signal drives FFT values and visibly/audibly modulates the scene (screenshot/video or logs as evidence).

### P3 — Stellar Nursery Transfer-Function / Hybrid Asset Spike

- Bounded investigation into the density→radiance/color/compositing transfer stage specifically (not more density/noise generation) — this is where Fable's review identified the actual bottleneck.
- Evaluate whether a Blender/offline asset or texture/plate workflow should supplement or replace parts of the pure-procedural density field. See "Hybrid Asset Pipeline" note below.
- Produce direct evidence (screenshots, before/after, FPS cost) comparing at least one procedural-only attempt against one baked/hybrid attempt.
- Hard cap: 2 bounded iterations per variant. A rejected variant is evidence for P4, not a trigger for another attempt.

### P4 — Stellar Nursery Architecture Decision Gate

- Based on P3 evidence, make an explicit, documented, user-signed decision: continue pure-procedural, or commit to a hybrid procedural + baked-asset architecture for Stellar Nursery.
- This is a decision checkpoint, not an implementation pass.

### P5 — Audio-Reactive Tuning with Real Signal

- Recalibrate `Tuning.cs` floor/max values against real guitar signal (from P2), not provisional constants.
- Collapse the `Tuning.cs` / `StellarNursery.cs` calibration-constant duplication into a single source of truth.

### P6 — Final Stellar Nursery Visual Polish

- Resume visual polish, now informed by: a real performance budget (P1), real target hardware behavior (P2), a resolved transfer-function/architecture approach (P3/P4), and real audio calibration (P5).
- This is where further shader/asset art-direction work belongs — not before.

## Hybrid asset pipeline (note, not yet implemented)

If pure-procedural Stellar Nursery continues to show weak visual payoff relative to shader complexity
(as seen across Visual Detail Passes 1 and 2), evaluate a hybrid approach in P3:

- Blender or other offline tools generate nebula plates, dust/mask textures, or density/star reference
  data ahead of time.
- Cosmic Engine's real-time layer focuses on what real-time is actually good at: compositing those
  assets, audio-reactive color/intensity/flow modulation, transitions, and performance-profile scaling —
  rather than generating 100% of visual complexity from hand-tuned procedural noise math per frame.
- This is a spike to evaluate in P3, not a commitment. Do not implement until P3/P4.

## Note — second world added (2026-07-06)

A second `IWorld` implementation, `World02_LavaLamp` (Lava Lamp Scene Draft v0.1, see `AUDIT.md` Entry 14),
was added alongside this P1-P6 sequence, not as part of it — it's an architecture proof (multi-world
support, `--world` CLI selection) using a deliberately cheap scene, not a Stellar Nursery pass. It does not
change the phase order above; Stellar Nursery visual polish is still parked pending P3/P4/P5 as described.

## Known risks carried into this roadmap

- **FPS variance is an open, unproven risk.** Diagnostics across the last three passes showed mostly
  stable 57-59 FPS readings, but also anomalous lows (~26 FPS, ~45 FPS) attributed to "system load"
  without evidence. P1 must investigate this with repeated runs per profile rather than continuing to
  assert it away.
- **No hardware-viability claim exists yet.** All testing to date has been on the dev machine
  (Intel Iris Graphics 6100), which is not the OptiPlex 5070 Micro stage target.
- **No real-audio validation exists yet.** Audio logs confirm only that the capture device opened, not
  that a real guitar signal has driven the visuals.
