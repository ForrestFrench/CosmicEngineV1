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

## Note — Lava Lamp upgraded to v0.2 (2026-07-11)

Lava Lamp's shader (`World02_LavaLamp/Shaders/lava_lamp.frag`) was upgraded from v0.1 (a "basic blob demo"
proving multi-world architecture) to v0.2, a more deliberate analog liquid-light/lava-lamp art pass —
organic lobed blobs, rise/fall/drift motion, layered depth, internal texture, a warm-dominant palette, and
subtle audio reactivity reusing existing uniforms (see `AUDIT.md` Entry 25). Still a prototype, not final
art — same "not part of the P1-P6 sequence" status as v0.1 (it's an architecture/second-scene proof, not a
Stellar Nursery pass), and this does not change the phase order above.

## Note — Dashboard Calibration Tab added, wired into scenes, and made persistent (2026-07-11 / 2026-07-12)

A new Calibration tab (Dashboard Calibration Tab v0.1, see `AUDIT.md` Entry 26) was added to the
dashboard: input channel selection, DAW-style level meters, per-input draggable response-curve
editors (eDRUMin-style), and manual gain/gate/smoothing/ceiling controls. **Status: implemented
and user-tested for meters/curves** — the user tested it on the Mac mini with a real Clarett
interface and guitar plugged into Input 1 and confirmed "The meters and the response curves work
as expected. This is a great outcome," including a live demonstration that the Sensitive preset
lifts the same raw playing dynamics into a meaningfully higher, more usable output range than
Linear. Calibrated Audio Reactivity Integration v0.1 (`AUDIT.md` Entry 27) then wired that
confirmed-working post-curve output into Stellar Nursery and Lava Lamp's audio reactivity as a
modest additive blend. **Status: implemented and committed** (`e163c16`), verified via a labeled
test-pulse mechanism (no real audio interface connected in this environment) — real-guitar
validation of the *scene response* specifically (not just the meters) still pending. Calibration
Presets v0.1 (`AUDIT.md` Entry 28) then added save/load/delete of named, full-setup presets
(routing + both inputs' controls and curves) to a local JSON file. **Status: implemented in this
pass**, verified end-to-end via direct endpoint calls including a deliberate corrupt-file-recovery
test and a Clarett-channel-fallback test; real-hardware validation of the saved/reloaded *feel*
still pending. Like the second world and the Scene Dashboard itself, all three passes are
workflow/infrastructure additions, not P1-P6 phases — none change the phase order above.
Follow-up work is tracked here rather than as a numbered phase, since it's calibration-layer
work, not Stellar Nursery visual polish:

### Dashboard Calibration Tab v0.2
- Per-interface calibration profiles (e.g. a saved profile for the Clarett practice rig vs. the
  Scarlett live rig) — largely delivered by Calibration Presets v0.1's named presets
  (`targetInterface` hint of Generic/Clarett/Scarlett); remaining v0.2-scope work is UI polish
  (e.g. filtering the preset dropdown by target interface) rather than new plumbing.
- Real per-channel Clarett multi-input validation and any UI fixes that surfaces — the channel
  selectors (and now preset routing fields) are channel-count-agnostic but only 2 channels (the
  current stereo capture limit) have actually been exercised.
- Tune `CalibrationEngine.CalibratedBlendWeight` (and per-scene mapping choices) against a real
  instrument signal — Entry 27's additive blend is a conservative first pass, not yet artistically
  validated the way the Calibration tab's meters/curves were.
- Preset UI polish: a `Notes` textarea (the field exists in the schema, no UI control yet), and
  making the manual-control sliders/curve graph reflect a loaded preset without waiting for the
  next status-poll tick.

### Dashboard Calibration Tab v0.3
- Guided auto-calibration: a "Calibrate Input A" / "Calibrate Both" button, a "play normally for
  10 seconds" capture window, and automatic estimation of noise floor, average level, peak
  reference, suggested gain/gate, and a suggested starting response curve from that sample —
  with manual fine-tuning available afterward via the existing controls, then optionally saved as
  a preset. Not implemented yet; documented here as a future item only.

### Future — real Clarett/Scarlett validation
Test calibrated *scene* response (not just the meters) with a real guitar/Clarett signal on the
Mac mini — connect the interface, launch a scene from the dashboard, adjust the response curve
while playing, and confirm the smoother/more controllable response translates all the way through
to the visuals. Also test the preset workflow specifically: save a preset while meters respond to
real playing, alter the curve, reload the preset, and confirm the post-curve output shape returns
to exactly what was saved. The piece of validation neither Entry 27's nor Entry 28's own
(mock/test-based) verification could substitute for.

### Future — true Clarett multi-channel capture
**Status: investigated (`AUDIT.md` Entry 29), not yet implemented.** The investigation confirmed,
directly on real hardware (a physically-connected Focusrite Clarett), that the current OpenAL
capture backend (Apple's system `OpenAL.framework` on macOS, no bundled OpenAL-soft) hard-rejects
every multichannel capture format tried (4ch/6ch/8ch all fail to open) — this is a native
OS/driver-level ceiling, not a fixable parameter. True 8-input Clarett capture therefore requires
a genuinely new capture backend, not a tweak to the existing one. Recommended path: a **hybrid
strategy** — keep the existing, already-tested OpenAL stereo path completely untouched for the
**Scarlett 2-input live mode** (it already works and stays the simple default), and add a new
multi-channel-capable backend specifically for **Clarett 8-input practice mode** on the Mac mini.
Between CoreAudio (Mac-only, no new dependency) and PortAudio/miniaudio (cross-platform, new
native dependency), PortAudio is the moderate-confidence recommendation — pending confirmation of
the OptiPlex live rig's actual OS, which Entry 29 could not establish and is the single
highest-value next fact to confirm. A proposed (not implemented) `IAudioCaptureBackend`
abstraction is documented in Entry 29's `REPORT.md`. Calibration Presets v0.1 already stores
channel indices beyond the current 2-channel limit without validation at save time, and its
`ApplyTo()` already handles out-of-range channel references safely with a warning — confirmed
during Entry 29's investigation — so existing saved presets will start working once a real
multi-channel backend lands, with no preset-format migration required. **Recommended immediate
next step: a small, timeboxed "Multi-Channel Audio Backend Spike"** — confirm the OptiPlex's OS,
then prototype a minimal capture test (PortAudio or CoreAudio, whichever the OS answer points to)
against the real Clarett, before committing to the full backend/abstraction implementation.

### Future — per-song/setlist preset assignment
Tie a calibration preset to a specific song or setlist entry so it loads automatically, rather
than always being a manual dashboard action. Not scoped or started - noted here as a natural
follow-up to Calibration Presets v0.1's manual load/save.

## Note — third world added, Wind Turbine Fire Phase 1 v0.1 (2026-07-12)

A third `IWorld` implementation, `World03_WindTurbineFire` (Wind Turbine Fire Phase 1 v0.1, see
`AUDIT.md` Entry 30), was added alongside this P1-P6 sequence, not as part of it — same status as
Lava Lamp above: a standalone scene addition (Phase 1 of a separately-approved architect plan), not
a Stellar Nursery pass, and it does not change the phase order above. Shader-only, fullscreen-quad,
same architecture family as Lava Lamp — no Blender, no mesh pipeline, no new engine infrastructure.
A dark, cold-dominant industrial-nightmare tableau (wind-turbine silhouettes against layered smoke,
a slow-building ember-glow horizon) that only warms as sustained musical energy accumulates.
**Status: implemented as a v0.1 visual prototype in this pass**, not yet reviewed/committed.
Explicitly Phase 1 only — flame tongues, heat distortion, camera-consume, and textures are deferred
to a future phase, tracked here rather than as a numbered P1-P6 item:

### Wind Turbine Fire Phase 2 (implemented, uncommitted — see `AUDIT.md` Entry 32)
Flame-tongue geometry at the horizon glow, heat-distortion/refraction over the glow band, and a
second "hot rim" smoke-underlighting term were implemented in Phase 2. A "camera-consume" effect at
peak heat remains not yet designed/implemented. Real-guitar validation of the full multi-minute
`uSceneHeat` timescale is still outstanding (Phase 1/2's evidence both used a temporary, fully-reverted
mock-peak/accelerated-rise-rate build, since a real 3-5 minute sustained-play ramp doesn't fit this
project's bounded-run discipline). Artistic tuning of `EmberCount`/`HeatRisePerSecondAtFullDrive`/
`HeatDecayPerSecond` and the glow/smoke color ramps against real playing dynamics is also still
outstanding, once real audio is available.

### Wind Turbine Fire Design Correction Pass 1 (accepted by user, committed `2a35904` — see `AUDIT.md` Entry 33)
A ChatGPT/user screenshot review of Phase 2 flagged embers reading as big/sparse/foreground-feeling and
flame tongues reading as a row of individually repeated cones rather than one continuous fire front.
Both corrected: embers reworked (smaller, clustered near the fire line, brightness-power-curved, base-
weighted); the 5 discrete flame tongues replaced by a single continuous scrolling height-field fire
front fused into the horizon glow band. User reviewed the evidence package and accepted it with no
corrections requested — committed. Camera-consume, real-guitar timescale validation, and further
artistic tuning against real playing dynamics remain outstanding, same as noted above.

### Wind Turbine Fire Refinement Pass 2 (accepted, committed `1615b12` — see `AUDIT.md` Entry 34)
Three small, controlled refinements on top of the accepted Design Correction Pass 1, per explicit user
request to preserve the current design rather than redesign it: (1) heat-wave distortion reworked so
distant background turbines get a separate, gentler, lower-frequency, height-tapered warp instead of
the fuller sky/smoke one, addressing user-reported "cartoon wobble" at high intensity; (2) ember density
now climbs progressively with fire intensity via a new per-ember activation threshold, while size/
placement/clustering are completely unchanged; (3) the fire front's height cap keeps growing past where
its gate saturates, and a new separate high-altitude haze layer lets the glow radiate higher into the
sky at high intensity without more flame geometry. No shared engine file was touched — confined entirely
to `wind_turbine_fire.frag`. Fire/smoke has now had three dedicated passes (Phase 2, Design Correction
Pass 1, this Refinement Pass 2), all now committed. Camera-consume and real-guitar timescale validation
remain outstanding, same as noted above.

### Wind Turbine Fire "no fire / no audio reaction" investigation (committed `1615b12` — see `AUDIT.md` Entry 38)
Documentation-only investigation, no source file changed in the final state: root-caused a user report of
"no fire, no audio reaction" to this machine's macOS default audio input being a virtual device ("Hue Sync
Audio"), not a real guitar interface — confirmed via live forced-heat evidence that the shader/scene render
fire correctly when actually driven. Committed together with Refinement Pass 2.

### Wind Turbine Fire Phase 3: turbine/geometry improvement (implemented, uncommitted — see `AUDIT.md` Entry 39)
The next phase of the originally-approved plan, now that fire/smoke has had three committed passes.
De-stiffens the turbines without touching fire/embers/smoke/distortion/ground-embedding/background-opacity/
the evolution slider/audio mapping: (1) blade motion blur — 3 angle samples around the current rotor angle,
averaged, ramping in with `uWindDrive` (already-existing uniform, none added) above a wind-drive threshold;
(2) subtle tower flex — the tower is now a 3-segment tapered-capsule chain that picks up a small, height-
increasing horizontal offset plus a slow per-turbine sway at high wind, returning fully upright at low
wind; (3) nacelle detail — a secondary capsule (tail/generator-housing stub) plus a subtle top-lit/
underside-shaded tint local to the nacelle's own footprint, foreground turbines only; (4)/(5) parallax +
haze grading — the farther background turbine nudged smaller/higher/hazier relative to the nearer one,
widening a previously narrow depth-separation gap, still comfortably under the transparency-bug ceiling
Phase 1.2 established. Confined entirely to `wind_turbine_fire.frag` — zero diff on `WindTurbineFireScene.cs`
or any shared engine file. Verified mathematically neutral at rest by construction, not just visually; no
measurable fps regression at either profile. Known limitation: the 3-sample blur technique can read as
several discrete "ghost" blade positions at high wind rather than one perfectly smooth blur — inherent to
the cheap single-pass approximation this architecture requires (no render-to-texture history buffer for
true accumulation), not a bug. No Blender/mesh work performed or newly required — that decision remains
deferred to a future review cycle only if turbines still fail visual review after this shader-only pass.
Camera-consume and real-guitar timescale validation remain outstanding, same as noted above. **Not
committed, not pushed — left uncommitted in the working tree pending review.**

## Note — Scene Dashboard added (2026-07-06)

A local Scene Dashboard (Scene Dashboard v0.1, see `AUDIT.md` Entry 15), served by the existing
`ControlServer` at `http://localhost:8080`, lets scenes be launched/switched by clicking instead of typing
`--world`/`--profile` CLI flags, with live in-process scene and profile switching. This is a workflow/
usability improvement, not a P1-P6 phase itself — it doesn't change the phase order above, and will grow
to expose whichever scenes P3/P6 eventually add without further roadmap changes.

## Known risks carried into this roadmap

- **FPS variance is an open, unproven risk.** Diagnostics across the last three passes showed mostly
  stable 57-59 FPS readings, but also anomalous lows (~26 FPS, ~45 FPS) attributed to "system load"
  without evidence. P1 must investigate this with repeated runs per profile rather than continuing to
  assert it away.
- **No hardware-viability claim exists yet.** All testing to date has been on the dev machine
  (Intel Iris Graphics 6100), which is not the OptiPlex 5070 Micro stage target.
- **No real-audio validation exists yet.** Audio logs confirm only that the capture device opened, not
  that a real guitar signal has driven the visuals.
