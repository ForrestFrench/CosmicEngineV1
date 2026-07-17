# VIDEO_SYSTEM_ARCHITECTURE.md — Video Atoms: Component Architecture

Companion to ENGINE_EVOLUTION.md. Design only — no implementation in this document. All components integrate
through the existing `IWorld`/`SceneRegistry` seam; no engine-loop changes. Artistic authority for every
component here: **COSMIC_ENGINE_PHILOSOPHY.md**.

**Rev 2 (post-ChatGPT review):** the decoder is now formally abstracted — the engine depends on
`IVideoDecoder`, never on ffmpeg. See §2.2a. Phase 1 uses only §2.1a/§2.2a/§2.2 plus a minimal single-uniform
composite; everything else in this document is later-phase design.

### 2.2a IVideoDecoder — the decode abstraction (mandatory seam)
- The engine (VideoTexture, scenes, compositor, director) references **only**:
  `IVideoDecoder { Open(path); Width/Height/Fps/DurationSec; bool TryAcquireFrame(...); Dispose(); }` —
  `TryAcquireFrame` is non-blocking and hands back the newest complete BGRA frame (or nothing), never stalling
  the render thread.
- `FfmpegPipeDecoder` is implementation #1 (subprocess + raw-BGRA pipe + reader thread + 3-frame ring buffer,
  per §2.2). Future replacements (hardware decode via VideoToolbox/QuickSync, LibVLC, image sequences) must
  require only a new implementation class and its construction site — zero changes elsewhere. Decoder choice
  is a construction-time concern; no scattered `if (ffmpeg)` logic anywhere.
- Process-lifecycle guarantees (cleanup on quit/switch/crash) are part of the *implementation's* contract;
  the interface stays transport-agnostic.

---

## 1. Asset pipeline (offline)

### 1.1 Ingestion CLI (`tools/ingest-clip`, offline only, never in the render path)
- Input: any source video file. Output: normalized asset + metadata sidecar in `Assets/Clips/<id>/`.
- Normalization via ffmpeg (system binary; no native interop in the app itself):
  - Scale to **1280x720 max** (letterbox/crop policy recorded in metadata), strip audio.
  - Transcode to **intra-only H.264** (`-g 1`, high bitrate). Rationale: every frame is a keyframe → cheap,
    seek-free, constant-cost software decode on both dev (M4 Pro) and target (UHD 630) machines; upgrade path
    to `-hwaccel` (VideoToolbox / QuickSync) later without changing assets.
  - Optional 960x540 proxy for the Safe profile (mirrors the existing RenderScale philosophy).
- Emits a metadata sidecar skeleton (below) with technical fields filled; curation fields hand-authored in v1.
- AI-assisted tagging (M8): an optional separate tool that fills the *same* curation fields. The schema is the
  contract; the tagger is replaceable and never required.

### 1.2 Metadata sidecar (`clip.json`, one per clip — follows the CalibrationPresetStore JSON pattern)
```json
{
  "id": "burning-turbines-01",
  "title": "Burning wind turbines, aerial",
  "file": "clip.mp4",  "proxy": "clip_540.mp4",
  "durationSec": 14.2, "fps": 30, "width": 1280, "height": 720,
  "loopable": true, "loopCrossfadeSec": 0.5,
  "themes": ["fire", "climate", "industrial", "destruction"],
  "emotion": { "dark": 0.9, "energy": 0.7, "beauty": 0.4, "unease": 0.8 },
  "motionLevel": 0.6,
  "dominantColors": ["#c4451c", "#1a1a20"],
  "pairsWithWorlds": ["WindTurbineFire"],
  "source": "user-provided", "license": "owned",
  "ingested": "2026-07-17T00:00:00Z", "sha256": "..."
}
```
- `themes` (open vocabulary) + `emotion` axes (fixed 0-1 axes) + `motionLevel` are the Scene Director's
  selection surface. `pairsWithWorlds` gives hand-curated procedural pairings until the director is smarter.
- `Assets/Clips/library.json` = generated index (id → path + summary fields), rebuilt by the CLI. Clip binaries
  stay out of git (`.gitignore`), consistent with how calibration preset data is handled; sidecars/index are
  small and *are* tracked so selection logic is reviewable.

## 2. Runtime components

### 2.1 TextureLoader (M1 — first texture path in the codebase)
- PNG/JPG → RGBA8 GL texture (`StbImageSharp` or equivalent pure-managed decoder; no native deps).
- Also provides `Texture2D` wrapper: handle, size, `Bind(unit)`, `Dispose()` — used by everything below and,
  as a side effect, finally unblocks the long-deferred Blender-silhouette idea for other scenes.

### 2.2 ClipPlayer (M2)
- One ffmpeg **subprocess** per active clip: `ffmpeg -i clip.mp4 -f rawvideo -pix_fmt bgra pipe:1`, stdout read
  by a dedicated background thread into a **3-frame ring buffer** (reuses the project's existing
  background-thread + volatile-handoff idiom from `AudioEngine.CaptureLoop`).
- Main/render thread: at most one `glTexSubImage2D` upload per engine frame (720p BGRA ≈ 3.5MB; at 30fps clip
  rate ≈ 105MB/s — trivial for shared-memory iGPUs). Never waits: if no new frame, re-uses last texture.
- Playback speed = frame-release pacing on the reader thread (0.25x-2x); reverse/pingpong deferred.
- Loop: process restart with `loopCrossfadeSec` handled by the compositor (brief 2-layer overlap) — deferred to
  M4 if simple hold-last-frame looping suffices for M2/M3.
- Failure modes (all non-fatal, all logged): file missing → director picks another clip / test scene shows
  magenta placeholder; decode stall → hold last frame; process exit → hold + flag in `/status`.

### 2.3 CinematicScene : IWorld + LayerCompositor (M3)
- A normal registered world (`Id: "Cinematic"`, launchable from dashboard/CLI like any other — inheriting
  profiles, seeds, diagnostics, calibration for free).
- Owns: 1-2 `ClipPlayer` slots, an optional **child `IWorld`** (any registered world, e.g. WindTurbineFire)
  rendered into a private `RenderTarget` each frame, and the composite pass.
- Composite = one fullscreen shader, layers as texture units, per-layer uniforms:
  `opacity`, `blendMode` (normal/add/screen/multiply), plus the EffectStack uniforms (2.4).
- Child world runs its own `Update(dt, audio)` — existing scenes stay fully audio-reactive *inside* the
  composite with zero changes to them. Child may run at Safe-profile scale under a High composite (perf lever).
- Layer order v1: video (back) → procedural child (front, typically add/screen at partial opacity) → effects.

### 2.4 EffectStack v1 (M4)
Uniform-driven, applied in the composite shader (no extra passes, no framebuffer ping-pong in v1):
grayscale mix, mirror X/Y, lift/gamma/gain color grade, vignette. Playback speed lives in ClipPlayer.
Audio-reactive hooks: effect params accept the existing calibrated/smoothed signals (Creator=light/energy →
grade intensity; Sculptor=motion → mirror/speed modulation), same additive-nudge conventions as scenes use.

### 2.5 SceneDirector (M6)
- Inputs: library index, current song state (the existing accumulator pattern — e.g. a director-level energy
  integrator fed by calibrated inputs, exactly like `_bloom`/`_sceneHeat`), operator intent (a dashboard
  "theme/mood" preset choice), and a seeded RNG (`COSMICENGINE_SEED` convention → reproducible shows for
  debugging, varied shows live).
- v1 selection: filter by theme/mood preset → weight by emotion-axis distance to current song state → weighted
  seeded pick, with a no-repeat window. Transition = timed crossfade between clip slots + grade shifts.
- Explicitly rule-based and inspectable in v1 (decisions logged to `/status` + audit trail); learned/AI
  selection is out of scope until after the M6 review.

## 3. Control surface (ControlServer — extended, never restructured)
- M2: `/status` gains clip fields (id, time, fps, buffer health). M4: manual endpoints (`/clip/load`,
  `/clip/speed`, effect toggles) + a minimal dashboard section, following the existing Tuning-slider wiring
  pattern. M6: director controls (theme preset, seed, skip/hold clip). All additions follow existing endpoint
  + HTML conventions; the calibration tab is untouched.

## 4. Performance budget (v1 targets; rule-15 distributions required at every milestone)
| Cost | Budget / note |
|---|---|
| Decode thread (720p intra-only, software) | One core fraction; measured at M2 on dev, M3.5 on OptiPlex. `-hwaccel` upgrade path if needed |
| Texture upload | ≤1 upload/frame/layer; ~3.5MB per 720p BGRA frame |
| Composite shader | Trivial (few texture taps + blend math) |
| Child procedural world | The dominant GPU cost — controlled via existing profiles/RenderScale, child-at-Safe lever |
| Hard rule | High profile ≥60fps on Mac mini for any shipped composite; OptiPlex numbers only from the OptiPlex (rule 7) |

## 5. Testing/diagnostics integration
- Every milestone testable via existing bounded modes: `--world VideoTest --smoke-test`,
  `--diagnostic motion` (frame-pacing/stall evidence: consecutive captures must show clip advance),
  `--diagnostic visual`, perf-sweep inclusion. New diagnostics only if a real gap appears (e.g. a
  `--diagnostic clip` bounded playback report at M2 if pacing evidence needs first-class support).
- Evidence culture unchanged: full-composite screenshots for all visibility claims (the Entry 47 lesson is
  binding on compositor work too — a layer that "renders" but is swamped in the composite is a failure).
