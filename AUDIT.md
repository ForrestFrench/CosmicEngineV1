# AUDIT.md

Governance/audit log for CosmicEngineV1. Each entry is a point-in-time snapshot of what's accepted, what's in flight, and what's unverified. The implementation model (Claude Code) may author entries but must not sign off on its own work — reviewer sign-off is left blank until a human or a separate reviewer model fills it in.

---

## Entry 1 — Phase 0 baseline

**Date:** 2026-07-05
**Author:** Claude Code (implementation engineer)
**Reviewer sign-off:** _(blank — pending review)_

### Governance docs present
- `CosmicEngine.App/CLAUDE.md`
- `AUDIT.md` (this file, newly created)
- No `PROJECT_STATE.md`, `ROADMAP.md`, or `IMPLEMENTATION_LOG.md` exist yet.

### Current accepted systems
Landed in commit `951bfee` ("Implement Cosmic Engine core"), with dead-code/backup cleanup in `99f40dc`:
- Engine core / game loop (`Engine/CosmicEngine.cs`, `Engine/IWorld.cs`, `Engine/Time.cs`)
- Camera drift system (`Engine/Camera.cs`)
- Audio capture + FFT pipeline (`Audio/AudioEngine.cs`, `Audio/AudioSignal.cs`)
- Render pipeline: fixed-resolution render target + blit-to-screen (`Rendering/RenderTarget.cs`, `Rendering/FullscreenQuad.cs`, `Rendering/ShaderProgram.cs`)
- Live tuning HTTP control server (`ControlServer.cs`, `Tuning.cs`)
- First world: Stellar Nursery (`Worlds/World01_StellarNursery/StellarNursery.cs` + its shaders)

### Candidate/in-progress systems
None currently in flight.

### Known unverified items
- Real guitar hardware input has not been re-verified since commit `951bfee` landed — the audio capture path has not been confirmed against a live two-channel signal post-commit.
- The tuning/calibration duplication between `Tuning.cs` and world-local constants in `StellarNursery.cs` (documented in `CLAUDE.md`) is still unresolved — the two are not guaranteed to stay in sync.

### Known hardware not yet tested
Per `CLAUDE.md`'s Runtime Dependencies section:
- **Audio interface**: OpenAL stereo capture device with a real two-channel guitar input (originally tuned for a Focusrite Clarett) — not yet re-verified against physical hardware since `951bfee`.
- **GPU/OpenGL**: OpenTK/OpenGL 4 rendering path — not yet re-verified on target display/performance hardware since `951bfee`.

---

## Entry 2 — Baseline Recovery Pass 1 — Shader Compile Blocker

**Date:** 2026-07-05
**Executor:** Claude Code / Sonnet (implementation engineer)
**Reviewer sign-off:** ChatGPT — ACCEPTED (2026-07-05)

### Blocker found
A baseline verification pass (prior to this entry) found that `dotnet run` crashed during `StellarNursery.Load()` with an unhandled exception — `stellar_nursery.frag` failed GLSL compilation, so the app never rendered a frame.

### Exact error messages
```text
ERROR: 0:65: Return type in redeclared function 'noise3' differs from previous declaration
ERROR: 0:99: Incompatible types (float and vec3) in assignment (and no available implicit conversion)
```

### Root cause
GLSL 330 still exposes the deprecated built-in noise functions `noise1`–`noise4`; the built-in `noise3(vec3)` returns `vec3`. The shader independently declared its own `float noise3(vec3 p)`, which the compiler treats as an incompatible redeclaration of the built-in — and the call site at line 99 then resolves against the built-in's `vec3` return type, producing the `float`/`vec3` assignment mismatch.

### Files changed
- `CosmicEngine.App/Worlds/World01_StellarNursery/Shaders/stellar_nursery.frag` — renamed the user-defined `noise3` function (and its one call site) to `noise3D` to stop colliding with the GLSL built-in. No other shader logic touched.
- `CosmicEngine.App/Engine/CosmicEngine.cs` — added OpenGL renderer/vendor/version/GLSL-version logging in `OnLoad()`.
- `CosmicEngine.App/Worlds/World01_StellarNursery/StellarNursery.cs` — added a `[Startup] StellarNursery loaded successfully` log line on successful load.
- `PROJECT_STATE.md` — created.
- `IMPLEMENTATION_LOG.md` / `ROADMAP.md` — created.

### Fix summary
Minimal rename to resolve the naming collision with GLSL's built-in `noise3`. No visual, architectural, or tuning changes.

### Build result
`dotnet build` — succeeded, 0 warnings, 0 errors.

### Run result
`dotnet run` — app starts, world loads, and renders continuously (confirmed via the per-second audio-level log printing repeatedly without crash) for the duration of the manual smoke-test run.

### OpenGL renderer/vendor/version output
```text
[OpenGL] Renderer: Intel(R) Iris(TM) Graphics 6100
[OpenGL] Vendor:   Intel Inc.
[OpenGL] Version:  4.1 INTEL-18.8.16
[OpenGL] GLSL:     4.10
```
(Captured on the current development machine — not yet validated on target/OptiPlex deployment hardware.)

### Known limitations
- No RenderScale/performance-profile system yet.
- No diagnostic-suite runner yet.
- No FPS baseline measurement exists.
- No physical OptiPlex validation performed.
- Real guitar/audio-interface hardware validation still pending (interface identity itself needs confirming — `Tuning.cs` references a Focusrite Clarett).
- Tuning/calibration duplication between `Tuning.cs` and `StellarNursery.cs` remains unresolved.

### Reviewer notes (ChatGPT, 2026-07-05)
Review scope: the GLSL startup crash, the minimal shader fix, OpenGL renderer logging, smoke-check logging, and the creation/update of `PROJECT_STATE.md`, `ROADMAP.md`, `IMPLEMENTATION_LOG.md`, and this file.

Accepted findings:
1. Runtime startup was blocked by a GLSL compile failure in `stellar_nursery.frag`.
2. Root cause correctly identified as a naming collision with GLSL's built-in deprecated `noise3` function family.
3. The fix — renaming the user-defined `noise3(vec3)` function and its call site to `noise3D` — is appropriately minimal and does not alter visual design or renderer architecture.
4. `dotnet build` succeeds with 0 warnings and 0 errors.
5. `dotnet run` now starts successfully, loads StellarNursery, and renders continuously during the smoke test.
6. OpenGL renderer/vendor/version/GLSL logging was added and confirms hardware-accelerated Intel Iris Graphics 6100 on the current development machine.
7. The new project docs accurately reflect the actual current repo state and do not claim nonexistent RenderScale, diagnostic-suite, profile, or FPS-baseline systems.

Acceptance decision: **ACCEPTED.**

This sign-off does not validate FPS performance, Live/Safe profile behavior, RenderScale behavior, diagnostic suite behavior, OptiPlex hardware performance, or real guitar/audio-interface behavior — those systems either do not exist yet in this committed codebase or remain untested.

Next required phase: **Runtime Diagnostics.**
