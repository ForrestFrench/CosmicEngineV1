# IMPLEMENTATION_LOG.md

Chronological log of implementation passes. Kept short — one entry per pass, pointing at commits/AUDIT.md for detail rather than duplicating it.

---

## 2026-07-05 — Baseline Recovery Pass 1

**Executor:** Claude Code / Sonnet

Fixed the runtime shader compile blocker that prevented the app from ever rendering a frame: `stellar_nursery.frag`'s `noise3` function collided with GLSL's deprecated built-in `noise3()`, which returns `vec3` instead of the intended `float`. Renamed to `noise3D` (smallest safe fix, no visual/logic changes).

Also added OpenGL renderer/vendor/version/GLSL-version startup logging (`Engine/CosmicEngine.cs`) and a `[Startup] StellarNursery loaded successfully` smoke-check log (`StellarNursery.cs`), and created `PROJECT_STATE.md` to reflect actual repo state. See `AUDIT.md` Entry 2 for full detail.

Result: `dotnet build` succeeds, `dotnet run` starts and renders continuously. No performance profile, diagnostic suite, or FPS baseline work was in scope for this pass.

---

## 2026-07-05 — Runtime Diagnostics Phase 1

**Executor:** Claude Code / Sonnet

Added minimal runtime measurement tooling: a once-per-second `[Perf]` log (fps, frame time, world name, window size, render-target size, audio capture status) in `Engine/CosmicEngine.cs`; a `--smoke-test` CLI mode that runs for a fixed 8s window, prints an fps/frame-time summary, and exits cleanly via `GameWindow.Close()`; and a `--diagnostic baseline` mode that does the same and additionally writes `DiagnosticReports/Baseline_<timestamp>/REPORT.md`. Added `AudioEngine.IsCapturing` as a minimal status accessor. `Program.cs` now passes `args` through to `CosmicEngineApp.Run(args)`. `DiagnosticReports/` added to `.gitignore` as generated output. See `AUDIT.md` "Runtime Diagnostics Phase 1" for full detail.

Result: `dotnet build` succeeds; normal `dotnet run`, `--smoke-test`, and `--diagnostic baseline` all verified working, no regression to visuals, audio, or controls. No RenderScale or performance-profile system was implemented — out of scope for this pass.

---

## 2026-07-05 — Baseline Recovery Pass 2 — Visible Frame and Process Cleanup

**Executor:** Claude Code / Sonnet

Addressed two user-reported issues: (1) Cosmic Engine sometimes left running after tests, and (2) the app shows a black screen. Root-caused the black screen: `stellar_nursery.frag`'s 3D camera-basis uniforms (`uCamPos`/`uCamForward`/`uCamRight`/`uCamUp`) and `uBassCombined` were declared in the shader but never set from `StellarNursery.cs`, defaulting to `vec3(0)` and producing a `normalize(vec3(0))` NaN that poisoned the raymarch. Fixed by setting a fixed camera basis (~200 ly out, looking at the origin, per the shader's own doc comment) and `uBassCombined` in `StellarNursery.Render()` — no shader logic or art direction changed.

Isolated the cause with a new `--diagnostic visual` mode (`Engine/CosmicEngine.cs`): three bounded 2s phases — solid color (isolates window/render-target/blit), an isolated debug gradient shader with no uniforms (`Diagnostics/Shaders/debug_gradient.{vert,frag}`, isolates the shader/quad pipeline), then normal StellarNursery — each captured as a PPM screenshot with average-luminance / non-black-% logged. All three came back non-black after the fix (StellarNursery: 0.318 avg luminance, 100% non-black).

For the orphaned-process complaint: confirmed `AudioEngine.Stop()` and `ControlServer.Stop()` were already called on unload and both background threads are daemon threads; added `Environment.Exit(0)` in `OnUnload` for all bounded modes as a hard guarantee, and added a standing rule to `CLAUDE.md` to prefer bounded commands and explicitly stop normal `dotnet run` before reporting completion. Verified via `ps aux` that no `CosmicEngine.App.dll` process remained after `--smoke-test`, `--diagnostic baseline`, or `--diagnostic visual`. See `AUDIT.md` "Baseline Recovery Pass 2" for full detail.

Result: `dotnet build` succeeds; all three bounded modes exit cleanly with exit code 0 and no orphaned process; StellarNursery now renders a visible, non-black frame. No RenderScale, Live/Safe profile system, or scene rewrite was in scope for this pass.

---

## 2026-07-05 — Stellar Nursery Regression Recovery

**Executor:** Claude Code / Sonnet

Corrected the framing from Pass 2/Phase 1: the original reported bug is **square/rectangular artifacts around stars**, not a black screen — the black/flat screen is a regression, and was not to be treated as the artistic baseline. Investigated full `git log -p --follow` history of `StellarNursery.cs` and `stellar_nursery.frag`: confirmed the camera-uniform NaN defect (same root cause as Pass 2) has existed since the very first implementation commit (`951bfee`) — no committed "last known good" state exists to roll back to. Verified the already-uncommitted camera-uniform/`uBassCombined` fix via a controlled `git stash`/`git stash pop` A/B test (pre-fix: perfectly flat single-value frame, min=max=avg luminance 0.051; post-fix: real spatial variance and visible structure) and left its logic unchanged.

Found and fixed an unrelated, currently-reproducible build break: `CosmicEngine.App.csproj` had no exclude for `DiagnosticReports/`, so a previously-generated review package's `source_context/*.cs` snapshot was picked up by the SDK's default compile glob, causing `dotnet build` to fail with 45 `CS0111` duplicate-member errors. Fixed with a `<Compile Remove="DiagnosticReports/**" />` / `<None Remove="DiagnosticReports/**" />` exclude; removed the stale extracted folder (kept its `.zip`).

Confirmed via screenshots that the original square/rectangular star-artifact bug is **still present** after recovery, and root-caused it (not fixed, out of scope): stars are placed by flooring the per-pixel ray direction into a voxel grid in `stellar_nursery.frag`, which projects to whole rectangular screen regions instead of points. Assembled `DiagnosticReports/StellarRegressionRecovery_<timestamp>.zip` for external review. See `AUDIT.md` Entry 6 for full detail.

Result: `dotnet build` succeeds (0 warnings/errors); `--smoke-test`, `--diagnostic baseline`, and `--diagnostic visual` all verified bounded and clean (no orphaned process) across the pre-fix/post-fix A/B test. Square/rectangular star artifacts remain the next concrete piece of work.

---

## 2026-07-05 — Star Artifact Fix — Replace Cell-Fill Stars with Point Stars

**Executor:** Claude Code / Sonnet

Fixed the original reported bug (square/rectangular/triangular star artifacts), root-caused in the prior pass. In `Worlds/World01_StellarNursery/Shaders/stellar_nursery.frag`, the old star block hashed a floored `rayDir` voxel cell and lit the *entire cell* above a threshold — since `rayDir` varies roughly linearly across a narrow-FOV screen, each voxel projected to a whole flat-shaded rectangular/triangular screen region. Replaced it with a `pointStarLayer(rayDir, cellFreq, density, radius, offset)` helper: still hashes a cell to decide whether it contains a star, but places a jittered center inside the cell and applies a `smoothstep(radius, 0.0, distance)` radial falloff (squared) bounded to exactly zero outside `radius` — a star is now a small soft dot, never a filled cell. `main()` sums two such layers at different frequencies/densities/radii. Change is isolated to the star block and one new helper function (+52/-8 lines, single file) — no changes to the raymarch loop, nebula density field, camera uniforms, audio uniforms, or tuning values.

Verified across multiple random seeds via bounded `--diagnostic visual` runs: shader compiles (a GLSL error would throw at `Load()`), stars render as small bounded dots in every run, no square/rectangular/triangular patches observed. Assembled `DiagnosticReports/StarArtifactFix_<timestamp>.zip` for external review. See `AUDIT.md` Entry 7 for full detail.

Result: `dotnet build` succeeds (0 warnings/errors); `--smoke-test` (avg fps 59.1) and `--diagnostic visual` both bounded and clean (no orphaned process, verified after every run). Star design is intentionally conservative — density/radius/color tuning is a candidate follow-up, not required by this fix.

---

## 2026-07-05 — Stellar Nursery Visual Recovery Pass 1

**Executor:** Claude Code / Sonnet

Addressed the reviewer-flagged limitation on the accepted star-artifact fix (commit `1051e27`): the scene rendered a visible frame with clean point stars, but the full nebula was still too dark/sparse (before this pass: avg luminance 0.082, range only 0.046, stddev 0.004, 0% of pixels above 0.10). Root cause: under silence `uBassCombined=0` collapses the brightness envelope to just `uDimLevel` (was 0.20), and `nebulaDensity()`'s 0.42 threshold combined with the dominant fbm octave's ~1000 ly scale (larger than the 400 ly march range) meant a fixed camera + a given `uSeed` effectively sampled one large-scale value for the whole frame, often landing below threshold almost everywhere.

Fix, isolated to 4 files: `Tuning.DimLevel` 0.20→0.55 (silence brightness floor); `nebulaDensity()` threshold 0.42→0.38 and raymarch extinction coefficient 0.80→0.35 (`stellar_nursery.frag`) — an intermediate attempt at threshold 0.30 alone was tried and rejected for overcorrecting into a uniform "fog wall" (transmittance saturated to zero within 1-2 march steps); a diagnostic-only `COSMICENGINE_SEED` env var override (`StellarNursery.Load()`) for reproducible captures; and two new bounded `--diagnostic visual` phases, `DensityDebug`/`RadianceDebug` (`Engine/CosmicEngine.cs`), gated by a new `StellarNursery.DebugMode` static field and a matching `uDebugMode` shader uniform, reusing already-computed transmittance/radiance to visualize density and raw emission structure in isolation. The accepted point-star fix was not touched — confirmed via close-up screenshot.

After: avg luminance 0.090, range 0.200 (seed 400) — visible soft cloud lobes with dark gaps and a clean bounded star, confirmed via full-frame, close-up, luminance, density, and radiance debug screenshots. Assembled `DiagnosticReports/StellarVisualRecovery_<timestamp>.zip` for external review. See `AUDIT.md` Entry 8 for full detail.

Result: `dotnet build` succeeds (0 warnings/errors); `--smoke-test` before/after (57.8→57.0 avg fps, no regression), `--diagnostic baseline`, and `--diagnostic visual` (5 phases) all bounded and clean (no orphaned process, verified after every run in a 5-seed tuning sweep plus before/after captures). Seed-dependent brightness variance is reduced but not eliminated — flagged as a known limitation, not fixed in this pass.
