# PROJECT_STATE.md

Point-in-time snapshot of the actual repo state. Update this file when the state changes materially — do not let it drift into aspirational territory.

**Last updated:** 2026-07-05 (Stellar Nursery Visual Recovery Pass 1 — see `AUDIT.md` Entry 8)

## Branch / status

- **Branch:** `cosmicos`
- **Star Artifact Fix (Entry 7): reviewed and ACCEPTED by ChatGPT** on 2026-07-05, committed as `1051e27`. Remains intact and unmodified — see below.
- **Visual Recovery Pass 1 (Entry 8, this pass, not yet committed):** addresses the reviewer's noted limitation that the post-star-fix scene was still too dark/sparse. Changes: `nebulaDensity()` threshold `0.42→0.38` and raymarch extinction coefficient `0.80→0.35` (`stellar_nursery.frag`), `Tuning.DimLevel` `0.20→0.55` (ambient floor under silence), a diagnostic-only `COSMICENGINE_SEED` env var override (`StellarNursery.cs`), and two new bounded `--diagnostic visual` phases (`DensityDebug`/`RadianceDebug`, `Engine/CosmicEngine.cs`) gated by a new `StellarNursery.DebugMode` static field. Awaiting ChatGPT review before commit.
- **Original bug fixed (Entry 7):** the square/rectangular star artifacts are fixed — stars now render as small bounded radial points. **Unaffected by this pass** — confirmed via `after_visual_recovery_closeup.png` in Entry 8's package.
- **Reviewer's important limitation from Entry 7, now addressed by this pass:** the post-star-fix scene was too dark/sparse; Entry 8 raises silence brightness and retunes density/extinction so the scene shows visible cloud structure (`after_visual_recovery_full_frame.png`: avg luminance 0.090, range 0.200, vs. before 0.082 avg / 0.046 range). Awaiting review of whether this is sufficient.

## Build / run status

- `dotnet build`: succeeds, 0 warnings, 0 errors.
- `dotnet run`: starts, logs OpenGL renderer/vendor/version, loads `StellarNursery`, and renders continuously. **StellarNursery now renders a visible (non-black) frame** — see Baseline Recovery Pass 2 below.
- `dotnet run -- --smoke-test`: runs for a fixed 8s window, prints an average-fps/frame-time summary, then exits cleanly (exit code 0, verified no orphaned process).
- `dotnet run -- --diagnostic baseline`: same bounded run, plus writes `DiagnosticReports/Baseline_<timestamp>/REPORT.md`.
- `dotnet run -- --diagnostic visual` (new): cycles solid-color → debug-gradient-shader → normal StellarNursery (2s each), capturing a PPM screenshot + average-luminance/non-black-% reading per phase, writes `DiagnosticReports/Visual_<timestamp>/REPORT.md`, exits cleanly. All three phases confirmed non-black on this machine (StellarNursery: 0.318 avg luminance, 100% non-black pixels).

## Baseline Recovery Pass 2 / Entry 6 — black screen root cause

`Worlds/World01_StellarNursery/Shaders/stellar_nursery.frag` declares 3D camera-basis uniforms (`uCamPos`, `uCamForward`, `uCamRight`, `uCamUp`) that `StellarNursery.cs` never set. Left at their GLSL zero-default, `uCamForward`/`uCamRight`/`uCamUp` were all `vec3(0)`, so the shader's `rayDir = normalize(uCamForward + ...)` evaluated to `normalize(vec3(0))` — a `0/0` NaN — which poisoned the entire raymarch and rendered black. `uBassCombined` (drives the final brightness envelope) was also never set, compounding the dimness. Fixed by setting a fixed camera basis (~200 ly out, looking at the origin, per the shader's own doc comment) and `uBassCombined = max(calibrated bass1, calibrated bass2)` in `StellarNursery.Render()`. No shader logic, art direction, or scene composition was changed.

**Entry 6 confirmed via full git history:** this defect has been present since the very first implementation commit (`951bfee`) — no commit ever set these uniforms. There is no committed "last known good" Stellar Nursery state to roll back to. Entry 6 also found and fixed an unrelated build-hygiene bug: `CosmicEngine.App.csproj` had no exclude for `DiagnosticReports/`, so a previously-generated review package's `source_context/*.cs` snapshot was picked up by the default compile glob and broke `dotnet build` with duplicate-symbol errors. Fixed with a `<Compile Remove="DiagnosticReports/**" />` exclude.

## Original bug status — square/rectangular star artifacts (FIXED, Entry 7)

The original reported bug — stars rendering as square/rectangular shapes instead of points — is fixed. `stellar_nursery.frag`'s star block no longer floors `rayDir` into a voxel and lights the whole cell; a new `pointStarLayer()` helper places a jittered star center inside each qualifying cell and applies a `smoothstep`-based radial falloff bounded to zero outside a small `radius`, so a star is now a small soft dot, never a filled cell. Confirmed visually across multiple random seeds in Entry 7's review package (`after_stellar_full_frame.png`, `after_star_closeup.png`, `after_luminance_debug.png`) — no square/rectangular/triangular patches in any run. Star design (density, radius, color) is intentionally conservative and may need a later artistic-polish pass.

## Visual Recovery Pass 1 / Entry 8 — dark/sparse scene root cause and fix

Under silence, `uBassCombined = 0` collapsed the shader's final brightness envelope (`color *= uDimLevel + uBassCombined * uBassBrightness`) to just `uDimLevel` (was `0.20`) — the single largest brightness suppressor. Separately, `nebulaDensity()`'s threshold (`0.42`) combined with the dominant fbm octave's ~1000 ly scale (larger than the 400 ly march range) meant a fixed camera + a given `uSeed` effectively sampled one large-scale value for the whole frame, often landing below threshold almost everywhere.

Fix: `Tuning.DimLevel` raised to `0.55`; `nebulaDensity()` threshold lowered to `0.38`; raymarch extinction coefficient lowered from `0.80` to `0.35` (an intermediate attempt — threshold 0.30 alone with the original 0.80 coefficient was tried and rejected: it saturated transmittance to zero within 1-2 march steps, producing a uniform "fog wall" instead of a dark wash — same flatness, different color). A diagnostic-only `COSMICENGINE_SEED` env var override was added to `StellarNursery.Load()` for reproducible captures; seed `400` is the current recommended known-reasonable value.

## Known limitation carried forward — seed-dependent brightness

Seed-dependent variation is reduced but not eliminated: 5 test seeds (100, 250, 400, 555, 777) with the new tuning ranged 0.09–0.45 avg luminance and 0.005–0.20 per-frame range — some seeds still land in a fairly flat region, though none reproduced the original near-zero-range "flat wash" or "solid fog wall" failure modes. Not fully fixed — would require camera-path or scene design changes, out of scope for a diagnostic-recovery pass.

## Architecture summary

- **Engine loop** (`Engine/CosmicEngine.cs`): owns the OpenTK window, the `Camera`, and the single active `IWorld`. Each frame builds an `AudioSignal` snapshot, updates the camera, renders the active world into a fixed 1280x720 `RenderTarget`, then blits that to the actual window framebuffer (shader cost is independent of window size). Parses `--smoke-test` / `--diagnostic baseline` / `--diagnostic visual` from `args` (passed through from `Program.cs`). All three bounded modes force `Environment.Exit(0)` in `OnUnload` to guarantee the process cannot outlive the window.
- **Worlds** (`Engine/IWorld.cs`, `Worlds/`): a world implements `Load()/Update()/Render()/Unload()`. Only one exists today: `Worlds/World01_StellarNursery/StellarNursery.cs`, a volumetric raymarched nebula shader reacting to two guitar audio channels (Guitar 1 = "Creator": energy/color; Guitar 2 = "Sculptor": density/structure). Now correctly sets the shader's 3D camera basis and `uBassCombined` (see above).
- **Audio pipeline** (`Audio/`): `AudioEngine` captures stereo audio via OpenAL on a background thread, runs an FFT per buffer (MathNet.Numerics), and exposes `Bass`/`Mid`/`Treble`/`Level` per channel, plus a static `IsCapturing` flag used by the perf logger.
- **Live tuning** (`ControlServer.cs`, `Tuning.cs`): a plain `HttpListener` on `http://localhost:8080` serves sliders bound to `Tuning.*` fields for live soundcheck adjustment.
- **Diagnostics** (`Diagnostics/Shaders/`): an isolated debug gradient shader (no uniforms, no dependency on world state) used only by `--diagnostic visual` to prove the shader/quad/uniform pipeline independently of any world. `--diagnostic visual` now runs 5 phases (Entry 8): SolidColor, Gradient, StellarNursery, DensityDebug, RadianceDebug — the latter two gated by `StellarNursery.DebugMode` (0 in all normal rendering) and a matching `uDebugMode` shader uniform, reusing the raymarch's already-computed transmittance/radiance to visualize density and raw emission structure in isolation.
- **Render resolution and raymarch step count are hardcoded** — 1280x720 in `CosmicEngine.cs`, 16 march steps in the shader. No dynamic scaling exists yet.

## Known limitations

- **No RenderScale / performance-profile system yet.** Render resolution and shader march/shadow step counts are still fixed constants, not configurable. Out of scope for this pass.
- **Diagnostic tooling is minimal, not a full suite.** Short (~2-8s) sample windows, no multi-scenario sweep, no historical comparison. Screenshots are raw PPM, not PNG (no image-encoding dependency added).
- **The fixed StellarNursery camera is static** — a minimal fix to stop the scene from being black, not a designed camera path. Camera motion/framing is a visual-polish decision, out of scope for this pass.
- **FPS baseline is measurable but not yet recorded as an accepted number.** Observed ~48-60 fps on the current dev machine (Intel Iris Graphics 6100); no target/pass-fail threshold agreed yet.
- **No physical OptiPlex validation yet.** All runtime verification so far has run on the current dev machine only.
- **Real guitar hardware input validation pending.** `Tuning.cs` documents calibration for a Focusrite Clarett interface; more recent references (outside this repo's code) mention a Scarlett 2i2 — whichever is the current physical interface, live two-channel guitar signal has not been re-verified against this build.
- **Tuning/calibration duplication is still unresolved.** `Tuning.cs` holds the live-tunable (control-server-backed) calibration constants; `StellarNursery.cs` keeps its own separate copy of floor/max constants used elsewhere in `Update()`. The two are not kept in sync automatically — documented in `CLAUDE.md`.
- **Dead top-level `Shaders/` folder has been removed** (commit `99f40dc`) — no longer a concern.
