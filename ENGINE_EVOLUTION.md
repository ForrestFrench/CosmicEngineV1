# ENGINE_EVOLUTION.md — Architectural Review: Video Atoms / Hybrid Cinematic Engine

**Role of this document:** lead-systems-architect review of the proposed "video atoms" direction, produced for
ChatGPT (overall project architect) before any implementation begins. No code was written or modified.
**Grounding:** based on direct inspection of the current codebase (2026-07-17), not the summary in the brief.

---

## 1. Verdict: is this the correct architectural evolution?

**Yes, with two significant corrections to the brief's framing.** The direction — curated cinematic clips
("video atoms") enhanced and transitioned by the existing procedural renderer, selected by a Scene Director,
with controlled randomness — is a sound evolution that matches how this project actually gets used (live
psych/prog shows, song-length arcs, one operator). It also directly addresses the recurring artistic ceiling
this project has hit twice now (Wind Turbine Fire and Cosmic Reef both went through multiple passes fighting
"procedural noise doesn't read as *things*"): real footage supplies the "things," procedural supplies the
motion, atmosphere, reactivity, and transitions. That division of labor is architecturally right.

**Correction 1 — the brief overstates what exists.** The listed "existing assets" need honest re-description,
because the migration path depends on it:

| Brief says | What actually exists |
|---|---|
| "Volumetric renderer" | One raymarched fragment shader (Stellar Nursery). Not an engine service. |
| "Particle system" | Analytic fixed-loop particles written inside individual scene shaders. Not an engine service. |
| "Bloom/post processing" | `uBloom` is a *scene-state accumulator* (song-arc progress scalar), not optical bloom. There is **no post-processing pipeline at all** — worlds render directly into one fixed 1280x720 `RenderTarget` which is blitted to the window. |
| "Camera system" | A 2D sine-drift offset/zoom helper consumed (optionally) by scene shaders. Not a 3D camera. |
| "Scene system, audio analysis, audio-reactive architecture, performance profiles, runtime tuning/diagnostics" | Accurate. These are real, mature engine services (SceneRegistry/IWorld, AudioEngine+FFT, CalibrationEngine, Safe/High profiles with RenderScale, ControlServer dashboard, bounded diagnostics, governance/audit culture). |

Implication: the compositor does not "absorb" a renderer, particle system, and post stack — those live *inside
scenes*. What the compositor actually needs to host is **scenes as layers**, which is a far smaller and safer
change than the brief implies.

**Correction 2 — the true gap is bigger than the brief implies in one specific place.** The engine has **zero
media pipeline**: no texture/image loading (the sole GL texture in the codebase is `RenderTarget`'s internal
FBO attachment), no video decode, no multi-layer blending, no asset-file conventions. Every prior plan on this
project has explicitly deferred "texture loading infrastructure" as its own future pass. The video-atom
direction now forces that build-out. This is genuinely new infrastructure — roughly three new subsystems — and
must be sequenced as infrastructure passes (house governance rules 11/12: infrastructure and shader-art never
mix in one pass).

## 2. Does anything need restructuring before implementation begins?

**No engine restructuring is required — that is the central architectural finding.** The safe insertion point
already exists: **the compositor enters the engine as just another `IWorld`.**

- `IWorld` is `Load()/Update(dt, AudioSignal)/Render()/Unload()`. A new `CinematicScene : IWorld` can own
  video layers, an effect stack, and optionally a *child* procedural world rendered to its own offscreen
  target, then composite everything in its `Render()`. The engine loop, scene registry, dashboard, profiles,
  calibration, seeds, and every diagnostic mode work on it unchanged, for free.
- Existing worlds are untouched and remain directly selectable forever. The "procedural renderer becomes
  another layer within the compositor" goal is achieved by *hosting*, not by rewriting: `CinematicScene`
  instantiates any registered world as a child and renders it to a private `RenderTarget` (the class already
  supports arbitrary instances), then samples that as a layer texture.

Two small, contained engine touches are needed (not restructures):
1. `RenderTarget` gains nothing; `CinematicScene` simply owns a second instance. Verify no global-state
   assumptions in its bind/unbind (quick audit item in M1).
2. `ShaderProgram` gains an `SetInt`-bound sampler convention for multiple texture units (it already sets
   uniforms; binding 2-3 textures is a per-scene concern, but confirm no hidden single-texture assumption).

**Housekeeping precondition (blocking):** the working tree currently holds the uncommitted Cosmic Reef Phase 1
pivot with a **failed 60fps High-profile gate and an open options memo (AUDIT.md Entry 48)**. That must be
dispositioned (optimize / trim scope / accept-and-park, then commit or revert) before any video-infrastructure
pass opens shared files. This is Milestone 0 and it is not optional — building a new media pipeline on top of
an unresolved failing working tree violates the project's own commit-by-intent discipline.

## 3. Systems that must remain untouched

- Engine loop (`CosmicEngine.cs` frame flow: AudioSignal → world.Update → world.Render → blit) — unchanged.
- `IWorld` contract — unchanged (the whole strategy depends on it).
- All four existing worlds' scene/shader code — unchanged (Wind Turbine Fire is explicitly user-accepted;
  World04 is mid-flight under its own review track).
- Audio pipeline (`AudioEngine`, FFT, `AudioSignal`) and the calibration layer (`CalibrationEngine`,
  `InputCalibration`, presets) — unchanged; the Scene Director *consumes* these signals, never modifies them.
- Dashboard/ControlServer architecture — extended with new endpoints/tabs later, never restructured.
- Performance profiles + RenderScale, `--seed`/`COSMICENGINE_SEED`, bounded diagnostics (`--smoke-test`,
  `--diagnostic visual/motion/perf-sweep`) — unchanged; every new subsystem must be testable through them.
- The AMFI/DLL-host launch workflow and `UseAppHost=false` — untouched.
- Governance: audit entries with blank reviewer sign-off, evidence packages, bounded runs, rules 1-16.

## 4. New systems to introduce

| System | What it is | Runtime or offline |
|---|---|---|
| **TextureLoader** | Image (PNG/JPG) → GL texture; the first texture-upload path in the codebase | Runtime (small) |
| **ClipPlayer** | Video file → per-frame GL texture stream. v1: ffmpeg subprocess piping raw BGRA frames to a background thread → small ring buffer → main-thread `glTexSubImage2D`. Never blocks the render thread; holds last frame on stall. | Runtime |
| **LayerCompositor** | Fragment-shader composite of N layers (video textures + child-world RenderTarget) with per-layer opacity/blend mode (normal/add/screen/multiply) | Runtime (inside `CinematicScene`) |
| **EffectStack v1** | Uniform-driven effects in the composite shader: grayscale, mirror X/Y, color grade (lift/gamma/gain), vignette; playback speed lives in ClipPlayer (decode rate), not the shader | Runtime |
| **ClipLibrary + metadata** | One JSON sidecar per clip (schema in VIDEO_SYSTEM_ARCHITECTURE.md) + a library index; follows the existing `CalibrationPresetStore` JSON-persistence pattern | Offline authoring, runtime read |
| **Ingestion CLI** | Offline tool: normalize any source clip via ffmpeg (fixed resolution/codec/duration trims), emit sidecar skeleton. AI-assisted tagging plugs in here **later** — same schema, optional tool | Offline only |
| **SceneDirector** | Selection logic: clip metadata × song state (existing accumulators + calibrated inputs) × seeded RNG (reuses the `COSMICENGINE_SEED` convention → "no two shows identical" *and* reproducible for debugging) | Runtime |

## 5. Dependencies between systems

```
TextureLoader ──► ClipPlayer ──► LayerCompositor/CinematicScene ──► EffectStack
                                        ▲                              │
      child IWorld (existing worlds) ───┘                              ▼
ClipLibrary/metadata ◄── Ingestion CLI              SceneDirector (needs: library, CinematicScene,
                                                     existing audio/calibration signals, seed convention)
```
Strictly buildable in that order; nothing downstream blocks anything upstream. The ingestion CLI and library
schema can be developed in parallel with M1-M3 since they are offline.

## 6. Safest migration path (no rewrite)

Strangler pattern, four properties: (1) every new capability arrives as a **new `IWorld`** registered in
`SceneRegistry` (`VideoTest`, then `Cinematic`), so the dashboard/CLI/diagnostics adopt it with zero engine
change; (2) existing scenes never gain video dependencies — if the video stack is deleted tomorrow, nothing
else breaks; (3) each subsystem lands as its own bounded infrastructure pass with evidence + audit review
(rules 9/11/12); (4) the user's existing reference video and the existing Wind Turbine Fire scene are the
validation pair from the *first composite milestone onward* (M3), so architecture is proven against real
assets months before the library/director exist.

## 7. Implementation order for lower-level models

M0 disposition → M1 TextureLoader (+static-image layer proof) → M2 ClipPlayer (+`VideoTest` scene, reference
video on screen) → **M3 Compositor** (video + child procedural world blended; first real "hybrid" frame) →
M3.5 OptiPlex checkpoint → M4 EffectStack + manual dashboard controls → M5 ClipLibrary/metadata/ingestion CLI
→ **M6 SceneDirector v1** → M7 formal OptiPlex/stage validation → M8 AI-assisted tagging + deferred polish.
Full details in IMPLEMENTATION_ROADMAP.md / MILESTONE_BREAKDOWN.md.

## 8. Milestones requiring architectural review before proceeding

- **After M2 (ClipPlayer):** decode approach validated on real hardware numbers before the compositor is built
  on top of it. Review: frame pacing evidence, CPU cost, stall behavior.
- **After M3 (Compositor):** the single most important gate — the hybrid model either proves out against the
  reference video + existing scene or the plan gets revised *here*, cheaply. Review: full-composite evidence,
  blend quality, perf table, M3.5 OptiPlex numbers if hardware is available.
- **After M6 (SceneDirector v1):** behavior/selection quality review with the user before any AI-assisted
  tagging investment (M8 depends on whether rule-based selection already feels good).

## 9. OptiPlex 5070 Micro performance risks (honest)

- **The OptiPlex has never run Cosmic Engine at all** (ROADMAP P2, unchanged). Every performance statement
  about it is currently conjecture; rule 7 forbids claims until it runs there. This is the single largest
  unknown in the whole plan — hence the M3.5 checkpoint as early as a composite exists.
- Its GPU (Intel UHD 630-class iGPU) is weak on fill-rate/ALU — the existing raymarched Stellar Nursery and
  the heavy World04 shader are the risk, **not** the video path. Its CPU-adjacent strength is exactly video:
  QuickSync hardware decode. The hybrid direction is therefore *well matched* to the target — a video layer +
  light procedural layer is likely cheaper there than today's pure heavy shaders.
- Mitigations already in-engine: fixed 1280x720 render target (shader cost independent of display), Safe
  profile RenderScale 0.5. New mitigations: normalize all clips to ≤720p at ingestion (never decode 4K live),
  intra-only codec for cheap software decode with an ffmpeg `-hwaccel` upgrade path, hard cap of 1 active
  video layer in v1 (2 only for crossfade windows), 30fps clip decode under a 60fps engine (upload every
  other frame), BGRA uploads (~3.5MB/frame; ~105MB/s at 30fps — trivial on shared-memory iGPU).
- Risk of *combined* load (decode thread + heavy child shader) is real on 4-core/low-TDP hardware: the
  compositor must support profile-scaled child worlds (a Safe-profile child even under a High composite).

## 10. Features to postpone

- AI auto-tagging of clips (M8; schema designed for it from day one, tool optional).
- Beat/onset detection and any new audio analysis (existing band energy + calibrated inputs are enough for v1).
- >2 simultaneous video layers; generative/AI video; live video input.
- LUT-based grading (matrix grade first), per-clip audio, network/remote control features.
- Blender/Hunyuan3D assets (unchanged position — though M1's TextureLoader finally unblocks that older idea
  as a free side effect).
- Any Cosmic Reef Phase 2+ art work: paused until the Entry 48 memo is resolved; art and infra tracks must not
  interleave in the same passes.
