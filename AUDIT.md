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

---

## Entry 3 — Runtime Diagnostics Phase 1

**Date:** 2026-07-05
**Executor:** Claude Code / Sonnet (implementation engineer)
**Reviewer sign-off:** ChatGPT — ACCEPTED (2026-07-05)

### Files changed
- `CosmicEngine.App/Engine/CosmicEngine.cs` — added `ParseArgs` (`--smoke-test`, `--diagnostic baseline`), a per-second `RunDiagnostics`/`[Perf]` logger, a bounded-run exit path via `GameWindow.Close()`, and `WriteDiagnosticReport` for the baseline report file. GL info strings are now cached in fields (still logged at `OnLoad` as before) so the report can reuse them.
- `CosmicEngine.App/Audio/AudioEngine.cs` — added `public static bool IsCapturing => _running;` (read-only accessor, no behavior change).
- `CosmicEngine.App/Program.cs` — now passes `args` through: `new CosmicEngineApp().Run(args);`.
- `.gitignore` — added `DiagnosticReports/` (generated output, same treatment as `bin/`/`obj/`).
- `PROJECT_STATE.md`, `IMPLEMENTATION_LOG.md` — updated to reflect this pass.

### What diagnostics were added
- **`[Perf]` log, once/second:** fps, frame time (ms), active world name (via `GetType().Name`), window client size, render-target size, and audio capture status (`AudioEngine.IsCapturing`).
- **`--smoke-test` CLI flag:** runs for a fixed 8s window, prints an average fps/frame-time summary, then calls `_window.Close()` for a clean exit (verified exit code 0).
- **`--diagnostic baseline` CLI flag:** same bounded run, plus writes `DiagnosticReports/Baseline_<timestamp>/REPORT.md` containing date/time, OpenGL renderer/vendor/version/GLSL, world loaded, sample window size, average fps, average frame time, and known limitations.
- No RenderScale, no performance-profile system, no shader/visual/audio/control changes.

### Build result
`dotnet build` — succeeded, 0 warnings, 0 errors.

### Run result
Verified all three modes on the current dev machine:
- Normal `dotnet run` — starts, renders continuously, `[Perf]` logs once/second, no early exit, no regression versus Baseline Recovery Pass 1 behavior.
- `dotnet run -- --smoke-test` — ran 8.0s (388 frames), printed a summary line, exited cleanly (exit code 0).
- `dotnet run -- --diagnostic baseline` — ran 8.0s (382 frames), printed a summary line, wrote `DiagnosticReports/Baseline_20260705_115857/REPORT.md`, exited cleanly (exit code 0).

### Sample `[Perf]` output
```text
[Perf] fps: 49.7 | frame: 20.1ms | world: StellarNursery | window: 1280x720 | target: 1280x720 | audio: capturing
```

### Known limitations
- No RenderScale/performance-profile system yet — out of scope for this pass.
- Diagnostic tooling is minimal: one report, one ~8s sample window, no screenshots, no multi-run comparison, no sustained-load/thermal test.
- FPS numbers observed so far (~48-50 fps on Intel Iris Graphics 6100) are not yet validated against any accepted target threshold.
- No physical OptiPlex validation performed.
- Real guitar/audio-interface hardware validation still pending — `audio: capturing` only confirms the capture device opened, not that a live guitar signal is present; interface identity itself needs confirming (`Tuning.cs` references a Focusrite Clarett).
- Tuning/calibration duplication between `Tuning.cs` and `StellarNursery.cs` remains unresolved (pre-existing, unchanged by this pass).

### Reviewer notes (ChatGPT, 2026-07-05)
Review scope: the added once-per-second `[Perf]` logger, `--smoke-test` mode, `--diagnostic baseline` mode, generated baseline report behavior, build/run results, updated project docs, and stated limitations.

Accepted findings:
1. `dotnet build` succeeds with 0 warnings and 0 errors.
2. Normal `dotnet run` starts and renders continuously without regression.
3. `dotnet run -- --smoke-test` runs for the bounded sample window and exits cleanly.
4. `dotnet run -- --diagnostic baseline` writes a baseline report under `DiagnosticReports/Baseline_<timestamp>/REPORT.md`.
5. Runtime diagnostics now log FPS, frame time, active world, window size, render-target size, and audio capture status once per second.
6. `DiagnosticReports/` was correctly added to `.gitignore` as generated output.
7. Documentation was updated honestly and does not claim the existence of RenderScale, performance profiles, screenshot capture, or sustained-load diagnostics.
8. No visual, shader, audio, or control behavior was intentionally changed.

Acceptance decision: **ACCEPTED.**

This sign-off does not validate Live/Safe profile behavior, RenderScale behavior, OptiPlex hardware performance, real guitar signal input, sustained-load thermal behavior, screenshot capture, or multi-profile diagnostic sweeps — those remain future phases.

Next required phase: **Live/Safe Profile System Phase 1.**

---

## Entry 4 — Baseline Recovery Pass 2 — Visible Frame and Process Cleanup

**Date:** 2026-07-05
**Executor:** Claude Code / Sonnet (implementation engineer)
**Reviewer sign-off:** _(blank — pending review)_

### User-reported issues
1. Cosmic Engine is sometimes left running after diagnostics or test runs, slowing down the user's computer until manually closed.
2. When Cosmic Engine runs, the user sees a black screen.

### Investigation steps
- Reviewed `OnUnload()`: confirmed `AudioEngine.Stop()` and `ControlServer.Stop()` were already both called, and both `AudioCaptureThread` and the `ControlServer` thread are `IsBackground = true` (daemon) threads — neither should keep the process alive on its own.
- Re-ran `--smoke-test` and `--diagnostic baseline` and checked `ps aux` immediately after each: exit code 0, no `CosmicEngine.App.dll` process remained in either case, before any code changes were made for this pass.
- For the black screen, isolated the render path per the required investigation list by building a new `--diagnostic visual` mode with three bounded phases, each capturing a screenshot + luminance reading:
  1. **Solid color** (no shader, direct `GL.Clear` on the render target) — isolates the window/render-target/blit path.
  2. **Debug gradient shader** (`Diagnostics/Shaders/debug_gradient.{vert,frag}`, no uniforms, no dependency on world state) — isolates the shader-compile/quad/uniform pipeline.
  3. **Normal StellarNursery** — the actual scene, rendered as-is.
- Grepped `stellar_nursery.frag` for every uniform it declares against every `_shader.SetFloat`/`SetVector3` call in `StellarNursery.cs`.

### Root cause
`stellar_nursery.frag` declares `uniform vec3 uCamPos, uCamForward, uCamRight, uCamUp;` — a 3D camera basis the raymarcher's `rayDir` calculation depends on — plus `uniform float uBassCombined;`. **None of these five uniforms were ever set anywhere in `StellarNursery.cs`.** OpenGL defaults unset uniforms to zero, so `uCamForward`/`uCamRight`/`uCamUp` were all `vec3(0,0,0)`. The shader computes:
```glsl
vec3 rayDir = normalize(uCamForward + ndc.x*aspect*tanHalfFov*uCamRight + ndc.y*tanHalfFov*uCamUp);
```
With all three inputs zero, this is `normalize(vec3(0))` — a `0/0` NaN — which propagates through the entire raymarch loop and renders black. `uBassCombined` being unset (defaulting to 0) additionally pinned the final brightness envelope (`color *= uDimLevel + uBassCombined * uBassBrightness`) to just `uDimLevel` (0.20), compounding the dimness even where the NaN issue didn't dominate.

This was an incomplete-migration bug, not a regression from Baseline Recovery Pass 1 or Runtime Diagnostics Phase 1 — the uniforms were missing before either of those passes touched this file.

### Fix applied
In `StellarNursery.cs`, `Render()` now sets:
- `uCamPos = (0, 0, -200)`, `uCamForward = (0, 0, 1)`, `uCamRight = (1, 0, 0)`, `uCamUp = (0, 1, 0)` — a fixed camera ~200 ly from the origin looking toward it, matching the shader's own doc comment ("Camera: ~200 ly from origin, looking forward through the nebula").
- `uBassCombined = max(calibrated uBass1, calibrated uBass2)`, reusing the same calibrated values already computed for `uBass1`/`uBass2`.

No shader logic, art direction, or scene composition was changed — this is strictly a "set the uniforms the shader already expected" fix.

Additionally, for the process-cleanup issue: `Environment.Exit(0)` was added at the end of `OnUnload()`, gated to only fire when `--smoke-test`, `--diagnostic baseline`, or `--diagnostic visual` was requested — a hard guarantee on top of the already-correct `Stop()`/daemon-thread behavior, directly targeting the user's report. `CLAUDE.md` was updated with the requested standing rule to prefer bounded commands and to explicitly stop normal `dotnet run` before reporting completion.

### Build result
`dotnet build` — succeeded, 0 warnings, 0 errors.

### Run/diagnostic result
- `dotnet run -- --smoke-test`: 8.0s, avg fps 57.9, exit code 0, no orphaned process.
- `dotnet run -- --diagnostic baseline`: 8.0s, avg fps 59.4, `REPORT.md` written, exit code 0, no orphaned process.
- `dotnet run -- --diagnostic visual`: all three phases confirmed non-black:
  - SolidColor: average luminance 0.285, 100.0% non-black pixels
  - Gradient: average luminance 0.500, 100.0% non-black pixels
  - StellarNursery: average luminance 0.318, 100.0% non-black pixels
  Screenshots (PPM) manually converted to PNG and visually inspected — solid magenta fill, a clean diagonal color gradient, and a dark-teal nebula field respectively, confirming the logged numbers.
- `ps aux` checked after each run: no `CosmicEngine.App.dll` process remained in any case.

### Screenshots/report path
`DiagnosticReports/Visual_20260705_122748/` — `SolidColor.ppm`, `Gradient.ppm`, `StellarNursery.ppm`, `REPORT.md` (this directory is gitignored, generated output, not committed).

### Known limitations
- The fixed StellarNursery camera is static and placed for correctness, not composed for visual polish — camera framing/motion remains a future visual-polish decision.
- Screenshots are raw PPM (NetPBM), not PNG — no image-encoding dependency was added to keep this pass minimal; convertible with e.g. `sips -s format png shot.ppm --out shot.png` on macOS.
- Visual-test phases are short (2s each) — a pass/fail visibility check, not a sustained visual QA pass.
- No RenderScale, Live/Safe profile system, or performance-profile work was in scope for this pass.
- No physical OptiPlex validation performed.
- Real guitar/audio-interface hardware validation still pending (unchanged from prior entries).
- Tuning/calibration duplication between `Tuning.cs` and `StellarNursery.cs` remains unresolved (unchanged from prior entries).

---

## Entry 5 — Diagnostic Review Package — Visible Frame / Black Screen Investigation

**Date:** 2026-07-05
**Executor:** Claude Code / Sonnet (implementation engineer)
**Reviewer sign-off:** _(blank — pending review)_

### Purpose
Assemble a single, self-contained diagnostic package (`DiagnosticReports/ReviewPackage_<timestamp>.zip`) for external (ChatGPT) review of the black-screen investigation, without requiring the user to manually copy terminal output or screenshots.

### Commands run
```bash
dotnet build
dotnet run -- --smoke-test
dotnet run -- --diagnostic baseline
dotnet run -- --diagnostic visual
```
All four succeeded (exit code 0 each); full output captured under `logs/` in the package.

### Small addition made to support this pass
`--diagnostic visual`'s `CaptureVisualFrame` (added in Baseline Recovery Pass 2) now also records **min** and **max** luminance per phase (previously only average + non-black %), and saves an extra grayscale luminance-debug PPM for the StellarNursery phase specifically (`StellarNursery_Luminance.ppm`). No other behavior changed. PPM screenshots were converted to PNG for the package using macOS's built-in `sips` (no new C# image-encoding dependency added, consistent with Pass 2's stated approach).

### Screenshots captured
`solid_color_render_path.png`, `gradient_or_colorbars_shader.png`, `stellar_nursery_normal.png`, `stellar_nursery_luminance_debug.png`, `final_frame.png` (identical to `stellar_nursery_normal.png` — see note below).

**Note on `final_frame.png`:** rather than launching a separate open-ended `dotnet run` and capturing a screenshot from it (which would have meant babysitting and force-closing an unbounded process, directly against this pass's critical process rule), the StellarNursery phase of the bounded `--diagnostic visual` run was reused for both `stellar_nursery_normal.png` and `final_frame.png`, since both are "the world's actual render output." Documented transparently rather than silently duplicated.

### Build/run results
- `dotnet build`: succeeded, 0 warnings, 0 errors.
- `dotnet run -- --smoke-test`: 8.0s, avg fps 58.5, exit code 0.
- `dotnet run -- --diagnostic baseline`: 8.0s, avg fps 58.6, `REPORT.md` written, exit code 0.
- `dotnet run -- --diagnostic visual`: all 3 phases 100% non-black by the >0.02 luminance threshold; screenshots + luminance-debug view + `REPORT.md` written.

### Black-screen finding — important nuance beyond Pass 2
The deterministic cause fixed in Baseline Recovery Pass 2 (unset camera-basis uniforms producing a `normalize(vec3(0))` NaN) is confirmed fixed. **However, running `--diagnostic visual` three times back-to-back with different random seeds produced StellarNursery average luminance of 0.318, 0.090, and 0.142 respectively** — and the 0.090 run had a luminance range of only 0.090–0.096 across the *entire frame*, which reads as visually flat/near-black to a human even though it is "100% non-black" by the >0.02 threshold used here. This strongly suggests a **secondary, seed-dependent issue**: the fixed static camera (`uCamPos = (0,0,-200)`, looking toward the origin) does not reliably intersect a visually-interesting density region of the noise field for every random seed, since `uSeed` shifts the entire hash-based noise field (`hash3`'s `p = fract(p * ... + uSeed * 0.017)`). Some seeds land the camera's fixed march range in a low-density, low-contrast part of the field. This was not investigated further or fixed in this pass — it is out of scope (no scene rewrite / no camera-path design in this diagnostic pass) but is flagged as the most likely explanation if the user still perceives a black or near-black screen intermittently after Pass 2.

### Zip created
`DiagnosticReports/ReviewPackage_20260705_163227.zip`

### Known limitations
- The >0.02 non-black-pixel threshold does not distinguish "visually flat and dim" from "genuinely varied and visible" — the min/max luminance spread added in this pass is a better signal and should be preferred going forward.
- Seed-to-seed StellarNursery brightness/contrast variance (0.090–0.318 avg luminance observed across 3 runs) is unresolved and is the most likely remaining source of a perceived black/near-black screen.
- Screenshots are single frames from short (2s) bounded phases, not a sustained visual QA pass.
- No RenderScale, Live/Safe profile system, or scene/camera-path redesign was in scope for this pass.
- No physical OptiPlex validation performed.
- Real guitar/audio-interface hardware validation still pending (unchanged from prior entries).

---

## Entry 6 — Stellar Nursery Regression Recovery

**Date:** 2026-07-05
**Executor:** Claude Code / Sonnet (implementation engineer)
**Reviewer sign-off:** _(blank — pending review)_

### Original issue
Square/rectangular artifacts around stars in the Stellar Nursery scene.

### Regression treated as separate from the original issue
Entries 4–5 above treated a flat/black scene as the primary defect. This pass was explicitly scoped to *not* treat that flat/dark output as the artistic baseline — it is a regression on top of whatever state the square-star-artifact bug was originally observed in. The original star-artifact bug was never the subject of Entries 4–5 and remains unfixed.

### Investigation performed
- Full `git log -p --follow` history of `StellarNursery.cs` and `stellar_nursery.frag` inspected. Only two commits ever touch either file (`951bfee`, `8719a03`); neither ever set `uCamPos`/`uCamForward`/`uCamRight`/`uCamUp`/`uBassCombined`. **No committed "last known good" state exists** — every commit since the initial implementation has the identical NaN/black defect, so a `git worktree` comparison against an older commit would not have shown a different result and was not performed (source-inspection evidence was conclusive).
- Ran a controlled A/B test: `git stash`ed the (already-uncommitted) camera-uniform fix in `StellarNursery.cs`, rebuilt, and ran bounded `--diagnostic visual` to confirm the pre-fix state is a perfectly flat single-value frame (min = max = avg luminance = 0.051). Restored the stash, rebuilt, and re-ran the same diagnostic to confirm the post-fix state has real spatial variance and visible nebula/star structure.
- Discovered and fixed a **build-hygiene regression** unrelated to the shader bug: `CosmicEngine.App.csproj` had no exclude for `DiagnosticReports/`, so a previously-extracted review package's `source_context/*.cs` snapshot files were being picked up by the SDK's default compile glob, causing `dotnet build` to fail with 45 `CS0111` duplicate-member errors. Fixed via a `<Compile Remove="DiagnosticReports/**" />` / `<None Remove="DiagnosticReports/**" />` exclude in the `.csproj`, and removed the stale extracted folder (the `.zip` deliverable itself was preserved).

### Root cause
Same root cause as Entry 4 (unset `uCamPos`/`uCamForward`/`uCamRight`/`uCamUp`/`uBassCombined` uniforms causing a `normalize(vec3(0))` NaN) — confirmed by A/B test in this pass, not a new finding. The fix for this was already sitting uncommitted in the working tree at the start of this pass; this pass verified it, did not alter its logic, and additionally fixed the unrelated build-hygiene issue above.

### Fix applied
1. No new shader/camera/uniform code changes — the existing uncommitted `uCamPos`/`uCamForward`/`uCamRight`/`uCamUp`/`uBassCombined` fix in `StellarNursery.cs` was verified correct via the A/B test and left as-is.
2. Added a `DiagnosticReports/` compile-exclude to `CosmicEngine.App.csproj` (build hygiene, see above).

### Screenshots / package path
`DiagnosticReports/StellarRegressionRecovery_20260705_170911.zip` (extracted folder: `DiagnosticReports/StellarRegressionRecovery_20260705_170911/`), containing `REPORT.md`, `screenshots/`, `logs/`, `source_context/`, `git/`, `audit/`.

### Build/run results
- `dotnet build`: succeeded, 0 warnings, 0 errors (after the csproj fix).
- `dotnet run -- --smoke-test`: 8.0s, avg fps 48.1, 386 frames, exit code 0, no orphaned process.
- `dotnet run -- --diagnostic baseline`: 8.0s, `REPORT.md` written, exit code 0, no orphaned process.
- `dotnet run -- --diagnostic visual`: run twice pre-fix/post-fix plus additional seed samples; all bounded, all exited cleanly, no orphaned process at any point (`ps aux` checked after every run).

### Remaining star-artifact status
**Square/rectangular star artifacts are still present** after this recovery pass — visible in `restored_stellar_after_recovery.png`, `stellar_nursery_luminance_debug.png`, and a cropped close-up `star_artifact_closeup.png` in the review package. Root cause identified (not fixed, out of scope for this pass): stars are placed by flooring the per-pixel ray direction into a voxel grid (`floor(rayDir * 18.0 + uCamPos * 0.003)`) and hash-testing each voxel — since ray direction varies roughly linearly across a narrow-FOV screen, each voxel projects to a whole flat-shaded rectangular region of screen space rather than a small point/sprite. The rest of the nebula scene (gradient, color, gas structure) is otherwise visually coherent.

### Known limitations
- No committed "last known good" Stellar Nursery state exists to reference — see investigation notes above.
- Seed-dependent brightness/contrast variance (avg luminance observed 0.051–0.318 across different seeds with the fixed camera) remains unresolved; some seeds still look flat/near-black or uniformly fogged even with the camera-uniform fix in place. Not fixed in this pass (would require camera-path or scene design changes, out of scope).
- No dedicated "stars-only" diagnostic render mode exists.
- Original star-artifact bug (square/rectangular star shapes) is root-caused but not fixed — recommended as the next concrete piece of work.
- No physical OptiPlex hardware validation performed. Audio interface was capturing but silent (levels 0.000) throughout — no real guitar signal exercised.

### Recommended next action
Fix the star-placement math in `stellar_nursery.frag` (the `floor(rayDir * 18.0 + uCamPos * 0.003)` voxel-hash block) so stars render as small soft points rather than flat-shaded rectangular cells.

---

## Entry 7 — Star Artifact Fix — Replace Cell-Fill Stars with Point Stars

**Date:** 2026-07-05
**Executor:** Claude Code / Sonnet (implementation engineer)
**Reviewer sign-off:** ChatGPT — **ACCEPTED** (2026-07-05)

**Review scope:** Reviewed `StarArtifactFix_20260705_172314.zip`, including `REPORT.md`, before/after screenshots, star close-up screenshot, shader diff, build/run results, process-cleanup notes, and audit/project-state context.

**Accepted findings:**
1. The original star artifact was correctly identified as whole-cell star rendering in `stellar_nursery.frag`, where `floor(rayDir * 18.0 + uCamPos * 0.003)` selected a ray-direction cell and applied color to the entire cell.
2. The resulting artifacts appeared as large square/rectangular/triangular patches rather than point stars.
3. The fix replaced the whole-cell star block with bounded radial point-star layers using finite `smoothstep` falloff.
4. The new stars render as small bounded points, confirmed by `after_star_closeup.png`.
5. The large square/rectangular/triangular star patches visible in `before_star_artifact.png` are not present in the after screenshots.
6. The fix is appropriately scoped to the star block and does not intentionally change the nebula raymarching, camera, audio, tuning, RenderScale, or profile systems.
7. `dotnet build` succeeds and bounded diagnostics exit without leaving an orphan process.

**Acceptance decision: ACCEPTED** for the original star-square artifact fix.

**Important limitation (reviewer note):** This sign-off does not accept the overall Stellar Nursery visual baseline as finished. The after full-frame image remains very dark/sparse and lacks strong nebula structure. That is deferred to a separate visual recovery/tuning pass (see Recommended next action, updated below).

### Original issue
Stars in Stellar Nursery rendered as large, hard-edged square/rectangular/triangular cells instead of small soft points (root-caused in Entry 6, fixed in this pass).

### Root cause
In `stellar_nursery.frag`, the star block hashed a floored `rayDir` voxel (`floor(rayDir * 18.0 + uCamPos * 0.003)`) and, above a threshold, added flat color to the **entire cell**. Since `rayDir` varies roughly linearly across a narrow-FOV screen, each voxel projected to a whole contiguous rectangular/triangular region of screen space with hard cell-boundary edges.

### Files changed
- `CosmicEngine.App/Worlds/World01_StellarNursery/Shaders/stellar_nursery.frag` (only file with logic changes this pass; +52/-8 lines, isolated to the star block and one new helper function)

### Fix summary
Added a `pointStarLayer(rayDir, cellFreq, density, radius, offset)` helper: still hashes a floored `rayDir` cell to decide whether it contains a star, but places a jittered star **center** inside the cell and computes a `smoothstep(radius, 0.0, distanceFromCenter)` radial falloff (squared), bounded to exactly zero outside `radius`. Replaced the old whole-cell block in `main()` with two `pointStarLayer` calls at different cell frequencies/densities/radii, summed and tinted with a simple hash-based cool/warm color. No changes to the raymarch loop, nebula density field, camera uniforms, audio uniforms, or tuning values.

### Build/run results
- `dotnet build`: succeeded, 0 warnings, 0 errors.
- `dotnet run -- --smoke-test`: 8.0s, avg fps 59.1, 473 frames, exit code 0, no orphaned process.
- `dotnet run -- --diagnostic visual`: run multiple times across different random seeds; shader compiled and ran in every case (a GLSL compile error would have thrown at `Load()`); stars confirmed rendering as small bounded dots with no square/rectangular/triangular cells in any run.
- `ps aux` checked after every run in this pass: no orphaned process at any point.

### Screenshots / package path
`DiagnosticReports/StarArtifactFix_20260705_172314.zip` (extracted folder: `DiagnosticReports/StarArtifactFix_20260705_172314/`), containing `REPORT.md`, `screenshots/`, `logs/`, `source_context/`, `git/`, `audit/`. Screenshots confirm: `before_star_artifact.png` (sharp rectangular/triangular blobs, carried over from Entry 6's capture), `after_stellar_full_frame.png` and `after_luminance_debug.png` (small scattered dots, no cells), `after_star_closeup.png` (single soft round dot with visible radial falloff), `solid_color_render_path.png` and `gradient_or_colorbars_shader.png` (render-path/shader-pipeline sanity checks, unaffected).

### Known limitations
- Star design is deliberately conservative (two simple radial layers, no twinkle/temperature variation) — may need later artistic polish, explicitly out of scope for this pass.
- Seed-dependent nebula framing/brightness (Entry 6) is unaffected by this fix and still varies run to run.
- No RenderScale / Live/Safe profile system yet.
- No physical OptiPlex hardware validation performed.
- No real guitar/audio-interface signal exercised beyond passive capture-device presence (levels 0.000 — silence, not a fault).

### Recommended next action
Per reviewer sign-off: commit this accepted star-artifact fix as its own commit, then perform a separate Stellar Nursery visual recovery/tuning pass focused on restoring visible nebula structure (the after full-frame screenshots are still very dark/sparse) and producing deterministic review screenshots (e.g. a fixed `uSeed` for diagnostic capture instead of the current per-load random seed).

---

## Entry 8 — Stellar Nursery Visual Recovery Pass 1

**Date:** 2026-07-05
**Executor:** Claude Code / Sonnet (implementation engineer)
**Reviewer sign-off:** ChatGPT — **ACCEPTED** (2026-07-05)

**Review scope:** Reviewed `StellarVisualRecovery_20260705_183249.zip`, including `REPORT.md`, before/after screenshots, density/radiance/luminance debug captures, git diff summaries, build/smoke-test results, process-cleanup notes, and project/audit documentation updates.

**Accepted findings:**
1. The prior post-star-fix Stellar Nursery output was effectively a flat dark/purple wash and was not an acceptable visual baseline.
2. The accepted bounded point-star fix remains intact; no square/rectangular/triangular star artifacts are visible in the after screenshots.
3. The recovery pass restored visible nebula structure without changing RenderScale, adding performance profiles, rewriting the scene, or reintroducing the old cell-fill star logic.
4. The density debug screenshot confirms meaningful opacity/density structure is present.
5. The radiance debug screenshot confirms nebula radiance exists independently of the final brightness envelope.
6. The full-frame after screenshot shows visible large-scale cloud structure and dark gaps rather than a flat blank frame.
7. `dotnet build` succeeds, smoke-test passes, diagnostics exit automatically, and no orphaned Cosmic Engine process remains.
8. FPS remained effectively stable, with no meaningful performance regression reported.

**Acceptance decision: ACCEPTED** as a visual recovery baseline.

**Important limitation (reviewer note):** This is not final Stellar Nursery art quality. The recovered scene is still soft, sparse, mostly cool/purple under silence, and dominated by large low-frequency blobs. It needs a dedicated visual detail/polish pass to add finer nebula texture, dust lanes, richer color variation, stronger depth, and more compelling composition while preserving the recovered baseline and accepted star fix.

**Next required phase:** Stellar Nursery Visual Detail Pass 1.

### Accepted star artifact fix remains intact
The bounded point-star fix (`pointStarLayer`, accepted in Entry 7 / commit `1051e27`) was **not modified** in this pass. Confirmed via close-up screenshot (`after_visual_recovery_closeup.png`) that stars are still small bounded soft points, no square/rectangular/triangular cells.

### Remaining issue addressed
The full Stellar Nursery frame was still too dark/sparse under silence — `before_visual_recovery_full_frame.png` measured avg luminance 0.082, range only 0.046, stddev 0.004, 0% of pixels above 0.10 luminance: a near-uniform dark wash, not a readable nebula.

### Investigation summary
- Density existed but rarely crossed the `nebulaDensity()` threshold (0.42) reliably; the dominant fbm octave varies on a ~1000 ly scale (larger than the 400 ly march range), so with a fixed camera, `uSeed` effectively picks one large-scale sample for the entire visible frame — confirming the seed-dependence flagged in Entry 6.
- Silence zeroes `uBassCombined`, collapsing the shader's final brightness envelope to just `uDimLevel` (was 0.20) — the single largest brightness suppressor.
- `Tuning.cs`/`StellarNursery.cs` duplicate `BassFloor`/`MidFloor`/`TrebleFloor`/`BassMax`/`MidMax`/`TrebleMax` (`StellarNursery.Render()`'s `Calibrate()` uses its own private consts, not `Tuning.cs`'s live copies) — real tech debt, confirmed not to be the cause of the current dimness (raw audio is 0 regardless under silence), left untouched.
- Camera position/orientation was not the bug itself — it's the interaction of a fixed camera with a seed-shifted large-scale density field that causes unreliable framing.

### Fix summary
Four files changed, all isolated to this pass's concern:
1. `nebulaDensity()` base threshold `0.42 → 0.38` (`stellar_nursery.frag`).
2. Raymarch extinction coefficient base `0.80 → 0.35` (`stellar_nursery.frag`) — this was the fix for an intermediate overcorrection (threshold 0.30 alone saturated transmittance to zero within 1-2 march steps, producing a uniform flat-colored "fog wall" instead of a dark wash — tried and rejected before landing on the 0.38/0.35 combination).
3. `Tuning.DimLevel` `0.20 → 0.55` (`Tuning.cs`) — ambient floor under silence, still leaves headroom for `uBassBrightness` with live audio.
4. `COSMICENGINE_SEED` diagnostic-only env var override (`StellarNursery.Load()`) for reproducible diagnostic captures; seed `400` selected as a representative known-reasonable value.
5. Two new bounded `--diagnostic visual` phases, `DensityDebug` and `RadianceDebug` (`Engine/CosmicEngine.cs`), gated by a new `StellarNursery.DebugMode` static int (0 in all normal rendering, explicitly reset at the top of the normal `StellarNursery` phase) and a matching `uDebugMode` shader uniform with two gamma-corrected early-return debug branches, reusing already-computed `transmittance`/`radiance` values.

No changes to the point-star block, camera uniforms, audio architecture, or controls.

### Screenshots / package path
`DiagnosticReports/StellarVisualRecovery_20260705_183249.zip` (extracted folder: `DiagnosticReports/StellarVisualRecovery_20260705_183249/`), containing `REPORT.md`, `screenshots/`, `logs/`, `source_context/`, `git/`, `audit/`. Screenshots: `before_visual_recovery_full_frame.png` + `before_visual_recovery_luminance.png` (flat dark wash), `after_visual_recovery_full_frame.png` (visible soft cloud lobes + one star, seed 400), `after_visual_recovery_closeup.png` (star still a clean bounded point), `after_luminance_debug.png`, `after_density_debug.png` (clean opacity map matching cloud shapes), `after_radiance_debug.png` (raw emission color, same shapes), `solid_color_render_path.png` and `gradient_or_colorbars_shader.png` (sanity checks, unaffected).

### Build/run results
- `dotnet build`: succeeded, 0 warnings, 0 errors.
- `dotnet run -- --smoke-test`: before 57.8 avg fps, after 57.0 avg fps — no measurable regression.
- `dotnet run -- --diagnostic baseline`: completed, `REPORT.md` written, exit code 0.
- `dotnet run -- --diagnostic visual`: run many times (before-capture, 5-seed tuning sweep, final after-capture with seed 400); all 5 phases (now including `DensityDebug`/`RadianceDebug`) completed cleanly every time.
- `ps aux` checked after every run: no orphaned process at any point.

### Known limitations
- Not final art polish — after-screenshots are still fairly dim/moody by design (avg luminance ~0.09 for seed 400); satisfies "visible and structurally readable," not a finished look.
- Warm/cool color variation under complete silence is limited (strong warm/hot blackbody colors need live Guitar 1 energy); the silent baseline shows mostly cool violet/indigo tones.
- Seed-dependent variation reduced but not eliminated: 5 test seeds ranged 0.09–0.45 avg luminance and 0.005–0.20 per-frame range with the new tuning; `COSMICENGINE_SEED=400` recommended as the known-reasonable diagnostic seed going forward.
- No RenderScale / Live/Safe profile system yet.
- No physical OptiPlex hardware validation performed.
- No real guitar/audio-interface signal exercised beyond passive capture-device presence (levels 0.000 — silence, not a fault) — this pass only validates the silent floor.
- Scene composition (camera path, palette, star/nebula balance) may still need a dedicated artistic pass later.

### Recommended next action
Human/reviewer live-viewing pass (`dotnet run`, real guitar input) to judge whether the new density/extinction/DimLevel balance holds up once live audio drives `uBass1`/`uBass2`/`uLevel1`/`uLevel2` on top of this silent baseline.
