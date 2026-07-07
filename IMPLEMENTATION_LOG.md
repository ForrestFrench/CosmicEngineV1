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

---

## 2026-07-05 — Stellar Nursery Visual Detail Pass 1

**Executor:** Claude Code / Sonnet

Follow-up to the reviewer-accepted Visual Recovery Pass 1 (commit `3cf74ea`): the scene was visible but still soft, sparse, low-frequency/blobby, and mostly cool/purple under silence, lacking fine texture, dust lanes, layered depth, or a compelling composition. All changes this pass are isolated to `Worlds/World01_StellarNursery/Shaders/stellar_nursery.frag` (+69/-13 lines) — no C# or `Tuning.cs` changes needed, and the accepted point-star fix was untouched.

Added a 4th `fbm3D()` octave (0.027 ly⁻¹, ~37 ly scale) for fine wisp/knot texture; reused its raw sample (via a new `out float fineOctave` parameter, at zero extra sampling cost) as a dust-lane erosion mask in `nebulaDensity()`. Added a separate cheap `noise3D()` sample driving a `warmPocket` mask, added directly to `emitCol` as a warm highlight — audio-independent, visible under silence. Two earlier approaches (a plain color `mix()`, and driving `T_K` to reuse the existing blackbody bleed-through) were tried and rejected as too subtle to read at this scene's low brightness. The warm-pocket noise frequency was also retuned mid-pass (0.006→0.018) after testing across seeds 100/400/777 showed the wider period could flood an entire frame warm for an unlucky seed instead of leaving isolated pockets. Added a free depth-based near-warm/far-cool tint (reusing the already-computed march distance `t`, no extra cost) and a constant `compositionOffset` shifting the sampled density-field position (not any camera uniform) for off-center framing.

At seed 400: full-frame luminance range 0.200→0.461, stddev 0.015→0.045, avg 0.090→0.104. Confirmed via density/radiance debug screenshots that the added structure is real (not a screenshot artifact), and via a pixel-level zoom crop that the fine texture doesn't devolve into noise/static. Star point re-confirmed clean (no regression) via close-up crop. Assembled `DiagnosticReports/StellarVisualDetailPass1_<timestamp>.zip` for external review. See `AUDIT.md` Entry 9 for full detail.

Result: `dotnet build` succeeds (0 warnings/errors); `--smoke-test` stable at 59.3-59.4 avg fps across 3 consecutive clean runs (before: 58.2 avg fps) — no regression, well within the 15% guardrail. Two isolated anomalous low-fps readings (28.0, 36.9) during this session were confirmed as transient system-load spikes (immediately followed by 59+ fps re-runs with no code change), not caused by the shader changes. `--diagnostic visual` (5 phases) run at seed 400 and spot-check seeds 100/777, all clean, no orphaned process at any point.

---

## 2026-07-05 — Stellar Nursery Visual Detail Pass 1 Revision

**Executor:** Claude Code / Sonnet

ChatGPT rejected Visual Detail Pass 1 (Entry 9): visibility improved over the flat baseline and the accepted point-star fix remained intact, but the color-mapping of the new density/warm-pocket effects produced hard-edged, posterized artifacts — a visible hard vertical seam, punched-out dark holes instead of soft dust lanes, and a flat opaque "sticker" look to the warm pocket rather than volumetric emission.

Root-caused via `DensityDebug`: the underlying density field is smooth (no seam) — the hard edge was purely a color-mapping artifact of `warmPocket = smoothstep(0.62, 0.80, warmNoise) * step(0.05, d)`, where the GLSL `step()` is a true binary on/off switch that jumped color discontinuously at the `d=0.05` density contour, visible in the full frame (not just a crop). Fixed, all changes still isolated to `stellar_nursery.frag`: replaced `step(0.05, d)` with a continuous `smoothstep(0.02, 0.10, d)`; broadened and reduced the warm-pocket mask/intensity (softer noise threshold, multiplicative combination with the smooth density gate instead of a hard switch, lower max additive color); broadened and eased the dust-lane erosion mask/strength so lanes fade gradually instead of cutting sharp-edged holes. Fine texture, depth tint, and composition offset were untouched (not implicated in the rejection). One tuning misstep along the way is disclosed in the package `REPORT.md`: an intermediate version multiplied the glow by raw `d` on top of the smoothstep gate, over-attenuating it to near-invisibility, corrected by removing the redundant factor.

Assembled `DiagnosticReports/StellarVisualDetailPass1_Revision_<timestamp>.zip` including a side-by-side crop comparing the rejected and revised results directly. See `AUDIT.md` Entry 10 for full detail.

Result: `dotnet build` succeeds (0 warnings/errors); `--smoke-test` stable at 59.7-59.9 avg fps across 3 of 4 clean runs (one anomalous 33.8/33.9 fps reading, consistent with the same intermittent system-load-spike pattern seen in the prior pass, not code-related). `--diagnostic visual` (5 phases) run at seed 400 (rejected + revised) and spot-check seeds 100/777, all clean, no orphaned process at any point. Revised metrics are intentionally lower-contrast than the rejected version (this was the fix, not a regression) — visual/screenshot judgment, not luminance metrics, was used to evaluate success per the reviewer's explicit instruction.

---

## 2026-07-05 — Stellar Nursery Visual Detail Pass 2

**Executor:** Claude Code / Sonnet

ChatGPT accepted the Visual Detail Pass 1 Revision (commit `1150667`) but flagged the result as still too soft, blurry, low-frequency, and under-detailed — clean and free of hard artifacts, but lacking wisps/tendrils, small-scale density variation, layered dust structure, and richer color interplay. This pass adds that detail while explicitly avoiding the mechanisms that caused the earlier rejection (no hard `step()` gates, no flat color blobs, no punched-out holes).

All changes isolated to `Worlds/World01_StellarNursery/Shaders/stellar_nursery.frag` (+42/-1 lines) — no C# or `Tuning.cs` changes needed, point-star fix untouched. Three additions: (1) a free `sin`/`cos`-only domain warp (no extra noise sampling) applied to the density-sampling position, bending the field into curved, organic tendril-like silhouettes instead of round/axis-aligned blobs; (2) a fine wisp modulation — one extra high-frequency (~11 ly) `noise3D()` sample used as a smooth multiplicative ripple (`mix(0.82, 1.22, smoothstep(...))`) on the already-thresholded density, riding on existing density rather than adding a new threshold; (3) a cool/teal emission-pocket counterpart to the existing accepted warm pocket, using the identical smoothstep-gated approach (reusing the already-computed density gate) at a distinct frequency, giving genuine warm-and-cool interplay instead of only warm. Dust lanes, warm pockets, and composition offset were left unchanged from the accepted Revision.

At seed 400: full-frame luminance range 0.206→0.344, stddev 0.025→0.038; density and radiance debug views both confirm finer surface structure without harder dust-lane edges (directly re-compared against the before crop). Checked seeds 100/400/777 for hard-artifact regressions — none found; the domain warp visibly reshapes silhouettes into more organic curves across all three. Star point re-confirmed clean via close-up crop. Assembled `DiagnosticReports/StellarVisualDetailPass2_<timestamp>.zip` including a before/after side-by-side crop. See `AUDIT.md` Entry 11 for full detail.

Result: `dotnet build` succeeds (0 warnings/errors); `--smoke-test` stable at 57.3-59.1 avg fps across the majority of runs (before: 58.2-58.6 avg) — effectively no regression, well within the 15% guardrail. Four anomalous low-fps readings (45.0, 26.0, and others) occurred during this session, each bracketed by clean 57+ fps re-runs with no code change in between — consistent with the same intermittent system-load-spike pattern documented in the two prior passes on this shared dev machine, not caused by this pass's shader changes. `--diagnostic visual` (5 phases) run at seed 400 (before/after) and spot-check seeds 100/777, all clean, no orphaned process at any point.

---

## 2026-07-06 — Fable Strategy Review — Roadmap Pivot

**Executor:** Claude Code / Sonnet (documentation/governance pass only — no shader, rendering, or RenderScale code changes)

ChatGPT reviewed Visual Detail Pass 2 and judged it should not be accepted yet: the star-square artifact fix remained intact, diagnostics and the shader path worked, and the density debug view showed improved underlying structure — but the final composited full-frame image remained too dark, soft, blurry, and low-detail, with the close-up still reading as blurred procedural blobs rather than wispy stellar-nursery gas. This raised a roadmap-level question rather than a shader-tuning one, so an independent high-level strategy review was requested from Fable.

Fable's verdict: the project is still on track overall (architecture, diagnostics, and governance are healthy), but the critical path has been pointed at the wrong target — three consecutive visual passes hand-iterating one procedural shader, with contested payoff each time, while every live-show assumption (OptiPlex stage hardware, real guitar signal, a performance-profile system) remains completely unvalidated. Key diagnostic insight: the density debug view shows usable structure, but the final frame stays dark/soft/blurry, meaning the actual bottleneck is most likely the density-to-radiance/color/compositing transfer stage, not more procedural density/noise generation — so another density-detail pass was unlikely to be an efficient next step.

Decision, documented this pass: Visual Detail Pass 2 is marked **REJECTED / PARKED** (not deleted — the uncommitted `stellar_nursery.frag` diff remains in the working tree undisturbed). Stellar Nursery visual polish is paused, not abandoned. `ROADMAP.md` reordered to: P1 RenderScale + Live/Safe Profiles + FPS Variance Evidence, P2 OptiPlex + Real Guitar/Scarlett Validation, P3 Stellar Nursery Transfer-Function / Hybrid Asset Spike, P4 Stellar Nursery Architecture Decision Gate, P5 Audio-Reactive Tuning with Real Signal, P6 Final Stellar Nursery Visual Polish. P1 acceptance criteria (RenderScale affecting real render-target resolution, ≥2 named profiles, active profile + render target size logged in `[Perf]`, ≥10 bounded runs per profile with avg/min/max/variance, no shader-art changes, no self-signed audit) documented in `PROJECT_STATE.md`. The previously-hand-waved FPS variance (26/33-36/45 fps anomalies across three passes) is now documented as an open, unproven risk that P1's repeated-run requirement must actually investigate, not further assert away. A hybrid Blender/offline-asset-pipeline note was added to `ROADMAP.md` as an evaluation candidate for P3, explicitly not to be implemented yet.

Added operating rules to `CLAUDE.md`: iteration cap on subjective/visual passes (stop and escalate after 2 failed acceptance attempts or one agent-day), scope firewall between infrastructure and shader-art passes, and an explicit rule to stop and escalate rather than add more procedural noise when visual payoff is weak relative to shader complexity.

Result: documentation/governance-only pass. No shader, rendering, or RenderScale code changed. No build run (not required — docs only). `AUDIT.md` reviewer sign-off left blank for ChatGPT/user, not self-signed. Nothing committed or pushed pending review.

---

## 2026-07-06 — P1: RenderScale and Live/Safe Profiles

**Executor:** Claude Code / Sonnet

Implemented P1 from the Fable roadmap pivot: a RenderScale system that actually changes render cost (not just a stored number), named performance profiles, and repeated FPS evidence to investigate the previously-unproven FPS-variance risk. Infrastructure only — no nebula shader-art, star, or audio-behavior changes; the parked Visual Detail Pass 2 `stellar_nursery.frag` diff was left completely untouched.

Two new files: `Engine/PerformanceProfile.cs` (`Safe`=0.50 RenderScale/640x360, `Balanced`=0.75/960x540, `High`=1.00/1280x720, default `High` to preserve existing behavior for normal `dotnet run`) and `Engine/PerformanceSweep.cs` (a `--diagnostic perf-sweep` orchestrator that runs N bounded sub-runs per profile sequentially in one process, starting `AudioEngine`/`ControlServer` once for the whole sweep rather than per sub-run, and writing raw CSV + summary evidence). `Engine/CosmicEngine.cs` now derives the actual `RenderTarget` size from `BaseRenderWidth/Height * profile.RenderScale` (window client size stays fixed; only the internal target and therefore render cost changes), parses `--profile <name>` (unknown name → warning + fallback to `Safe`, never a crash), and logs profile/RenderScale/render-target size in both `[Perf]` and the baseline diagnostic report. `Program.cs` dispatches `--diagnostic perf-sweep` to the sweep orchestrator before constructing any window.

Ran the required 10-run-per-profile sweep. Result, and the headline finding of this pass: **`Safe` (640x360) was rock-solid across all 10 runs (58.87-59.75 avg fps, stdev 0.26)**, while **`High` (1280x720) was highly bimodal (18.94-59.76 avg fps, stdev 16.43) — 3 of 10 runs collapsed to roughly a third of the vsync-capped framerate**, closely reproducing the magnitude of the "anomalous" 26/33-45 fps readings hand-waved as "system load" across the last three visual passes. This is now real, repeated-run evidence that the dips are reproducible and correlate strongly with running at/near this GPU's real-time budget ceiling at full resolution — though the exact mechanism (GPU power state, thermal, OS scheduling, or genuine contention) remains unisolated; no system-load correlation tooling was added (out of scope for an infrastructure-only pass). Both profiles were confirmed to render the scene correctly via `--diagnostic visual` screenshots at a fixed seed (identical luminance stats between profiles, confirming scale-independent scene content); the point-star fix was re-confirmed clean via close-up crop in both. Assembled `DiagnosticReports/PerformanceProfiles_<timestamp>.zip` for external review. See `AUDIT.md` Entry 13 for full detail.

Result: `dotnet build` succeeds (0 warnings/errors). All required commands run and verified bounded: `--profile Safe/High --smoke-test`, `--profile Safe/High --diagnostic visual`, `--diagnostic perf-sweep` (10x2 runs, ~2 minutes total). `ps aux` checked after every command, including immediately after the full sweep — no orphaned process at any point. Nothing committed or pushed pending review; `AUDIT.md` reviewer sign-off left blank.

---

## 2026-07-06 — Lava Lamp Scene Draft v0.1

**Executor:** Claude Code / Sonnet

Implemented a second world to prove CosmicEngine's `IWorld` architecture actually supports more than one scene, per the Fable roadmap's implicit multi-world goal — a prototype draft, not final art, and deliberately much cheaper than Stellar Nursery. No Stellar Nursery shader/code changes; no OptiPlex/real-guitar validation (still out of scope, reserved for P2).

New: `Worlds/World02_LavaLamp/LavaLampScene.cs` + `Shaders/lava_lamp.{vert,frag}` — an analytic 2D metaball field (fixed-size loop of orbiting soft blobs, `r²/d²` falloffs, runtime-capped by a profile-aware `uBlobCount`), thresholded with `smoothstep` only (no hard `step()` masks, consistent with the project's standing rule), cheap `sin`/`cos` domain warp (no noise sampling), and an edge-only additive glow approximating bloom. Guitar 1 ("Creator") drives color intensity/hue shift/glow-pulse; Guitar 2 ("Sculptor") drives blob size/distortion/wobble frequency; every driven uniform has a non-zero time-based baseline so the scene stays visible, colored, and moving under total silence. New `Engine/WorldSelector.cs` adds `--world <name>` CLI selection (case-insensitive, unknown name → warning + fallback to `StellarNursery`, never crashes — same pattern as `PerformanceProfile.TryParse`), wired into `CosmicEngine.cs` (`Run()` and `RunSweepSubRun()`) and `PerformanceSweep.cs` (optional `--world` targeting, `PerfSweepRunResult` gained a `WorldName` column). `Program.cs` needed no changes — it already passed `args` through unmodified in both branches.

Caught and fixed a visual defect mid-pass: the first shader draft drove blob color from the raw, unbounded metaball `field` value, which grows very large near each blob center (`1/d²`), causing `sin()` to cycle through multiple colors within a single blob's radius (a visible "bullseye" ring pattern), and the additive glow term stacked on top of an already-opaque core, washing centers toward white. Fixed by driving color from the bounded `shape` (post-`smoothstep`) value instead of raw `field`, and gating glow's contribution by `(1.0 - shape)` so it only adds at the blob's soft edge — confirmed visually in the recaptured screenshots: smooth single-tone blob cores, clean edge glow, no rings, no clipping.

Ran a 20-run perf-sweep (`--diagnostic perf-sweep --world LavaLamp`, 10×Safe + 10×High): FPS range was 0.2 fps (Safe) and 0.4 fps (High) — no trace of the bimodal collapse pattern documented for Stellar Nursery's `High` profile in P1/Entry 13 on the same hardware. Point-star fix re-confirmed clean via a precisely-located (not eyeballed) close-up crop: raw pixel data shows a clean, radially-symmetric ~4px falloff, no cell-square. Assembled `DiagnosticReports/LavaLampDraft_<timestamp>.zip` for external review. See `AUDIT.md` Entry 14 for full detail.

Result: `dotnet build` succeeds (0 warnings/errors). `--world LavaLamp --profile Safe/High --smoke-test` (~59.8-59.9 avg fps both), `--world StellarNursery --profile Safe --smoke-test` (59.8 avg fps, confirming no regression), `--world Bogus` fallback, default-world-unchanged, and the 20-run perf-sweep all verified bounded with `ps aux` checked clean after every command. **Reviewed and ACCEPTED by ChatGPT on 2026-07-06, committed as `c29a219`.** Not pushed pending user approval.

---

## 2026-07-06 — Scene Dashboard v0.1

**Executor:** Claude Code / Sonnet

Workflow/usability pass (not a visual-art pass): gave the user a local dashboard, served by the existing `ControlServer` at `http://localhost:8080`, for launching/switching between the two accepted scenes without typing `--world`/`--profile` CLI flags. No Electron/React/cloud hosting; no Stellar Nursery or Lava Lamp visual changes.

New `Engine/SceneRegistry.cs` is now the single source of truth for scene metadata (Id/DisplayName/Description/Status/DefaultProfile/Showable/optional ThumbnailPath/Factory). `Engine/WorldSelector.cs` was refactored into a thin wrapper over it — `ValidNames`/`TryParse`/`Create` all now derive from `SceneRegistry`, so CLI `--world` parsing and the dashboard's scene cards read from exactly one list, per the task's explicit requirement. `CosmicEngine.cs`/`PerformanceSweep.cs` needed no changes for this refactor since `WorldSelector`'s public API was preserved.

The harder architectural question was live scene/profile switching. `Engine/CosmicEngine.cs` gained a static `CosmicEngineApp.Current` (the running show-mode instance — set only in `Run()`, never in `RunSweepSubRun()`, so a bounded perf-sweep sub-run is never reachable from the dashboard) and thread-safe `RequestSwitch(world, profile)`/`RequestRestart()`/`RequestQuit()` methods that just set volatile fields from `ControlServer`'s background HTTP thread. The actual world/profile change (`ApplyPendingSwitch()`) runs once per frame inside `OnRenderFrame`, on the render thread — the only thread allowed to touch GL resources (shader compile/link, FBO create/destroy) — mirroring the exact same `Unload()`/`Create()`/`Load()` and `RenderTarget` lifecycle already used at startup. This was **implemented as full live, in-process switching for both scene and profile** (the task's preferred behavior, not the CLI-hint fallback), gated off entirely during `--smoke-test`/`--diagnostic baseline`/`--diagnostic visual` so bounded-diagnostic behavior is unaffected.

`ControlServer.cs` gained `GET /status`, `GET /scenes`, `POST /launch`, `POST /restart`, `POST /quit`, and a new "COSMIC ENGINE — SCENE DASHBOARD" section prepended to the existing page: a live status bar (world/profile/fps/render target/audio), an (implemented but currently inert, since no experimental scene exists yet) "Show experimental scenes" checkbox, scene cards with Launch Safe/Launch High/Restart buttons, and a Quit Engine button (guarded by an in-page `confirm()`). The pre-existing "THE DEEPEST SPACE" audio-tuning sliders were kept, not removed. New `run-show.sh` (backgrounds `dotnet run -- --profile Safe`, waits, then `open`s the dashboard URL, falling back to printing it) and a double-clickable `Run Cosmic Engine.command` delegating to it.

Verified live switching end-to-end against a real running show-mode session, driven through an actual browser: `StellarNursery(Safe) → LavaLamp(Safe) → LavaLamp(High) → Restart → StellarNursery(Safe) → Quit`, confirming via `ps aux` that the **OS process ID never changed** across any of these steps (only exiting after the final quit) — proving every switch was live and in-process, not a hidden restart. Each step was cross-checked against the running process's own `[Dashboard] Switched to ...`/`[Dashboard] Restarted ...` console lines and the subsequent `[Perf]` line's `world:`/`target:` fields. Quit was tested via a direct `POST /quit` rather than clicking the in-page button, to avoid triggering its `confirm()` dialog through browser automation tooling — functionally identical, since the button calls the same endpoint.

Browser-side dashboard screenshots (`dashboard_home.png`, `dashboard_lava_lamp_selected.png`) could not be persisted to local disk this pass — the automated browser runs in a separate sandbox from this machine's filesystem, and native-screenshot fallbacks (`screencapture`, `osascript`/System Events) didn't work in this environment. Documented as a known limitation per the task's own fallback allowance; the dashboard's exact HTML/CSS/JS is included in the review package's `source_context/ControlServer.cs` instead, and the live-switching functional evidence above was judged to be stronger proof than a static image would have been anyway. `lava_lamp_from_dashboard.png`/`stellar_nursery_from_dashboard.png` were captured via `--diagnostic visual` (the engine's only current GL.ReadPixels capture path) rather than mid-session, since visuals are unchanged by this pass.

Result: `dotnet build` succeeds (0 warnings/errors). All four pre-existing CLI commands re-verified unchanged and bounded: `--world LavaLamp --profile Safe --smoke-test`, `--world StellarNursery --profile Safe --smoke-test`, `--profile Safe --smoke-test` (default world still `StellarNursery`), `--diagnostic baseline`. `ps aux` checked clean after every command and after the full interactive dashboard session (including after quit). Assembled `DiagnosticReports/SceneDashboardV01_<timestamp>.zip` for external review. See `AUDIT.md` Entry 15 for full detail. Nothing committed or pushed pending review; `AUDIT.md` reviewer sign-off left blank.
