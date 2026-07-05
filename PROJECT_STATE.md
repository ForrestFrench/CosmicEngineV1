# PROJECT_STATE.md

Point-in-time snapshot of the actual repo state. Update this file when the state changes materially — do not let it drift into aspirational territory.

**Last updated:** 2026-07-05 (Star Artifact Fix — see `AUDIT.md` Entry 7)

## Branch / status

- **Branch:** `cosmicos`
- **Star Artifact Fix (Entry 7): reviewed and ACCEPTED by ChatGPT** on 2026-07-05 — see `AUDIT.md` Entry 7 for full sign-off. Being committed as its own commit per reviewer instruction.
- **Commit contents:** `StellarNursery.cs` camera-uniform/`uBassCombined` fix, `--diagnostic visual` mode + screenshot/luminance capture in `Engine/CosmicEngine.cs`, `Diagnostics/Shaders/debug_gradient.{vert,frag}`, `Environment.Exit(0)` safety net for bounded modes, a `CosmicEngine.App.csproj` compile-exclude for `DiagnosticReports/`, `stellar_nursery.frag`'s star block replaced with bounded point stars, plus `CLAUDE.md`, this file, `AUDIT.md`, and `IMPLEMENTATION_LOG.md`.
- **Original bug fixed (Entry 7):** the square/rectangular star artifacts are fixed — stars now render as small bounded radial points. See below.
- **Reviewer's important limitation:** acceptance of the star fix does **not** extend to the overall Stellar Nursery visual baseline — the after full-frame screenshots are still very dark/sparse and lack strong nebula structure. **Next required phase:** a separate Stellar Nursery visual recovery/tuning pass (restore visible nebula structure, deterministic review screenshots) — not yet started.

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

## Known limitation carried forward — seed-dependent brightness

With the camera-uniform fix in place, StellarNursery's average frame luminance still varies a lot from run to run purely due to the random per-load `uSeed` (observed 0.051–0.318 across sessions) because the camera position is fixed while `uSeed` shifts the whole procedural density field. Some seeds land the camera in a near-empty region (looks flat/near-black) or a fully-dense region (looks like a uniform gray fog wall) rather than a visually interesting boundary region. Not fixed — would require camera-path or scene design changes, out of scope for a diagnostic-recovery pass.

## Architecture summary

- **Engine loop** (`Engine/CosmicEngine.cs`): owns the OpenTK window, the `Camera`, and the single active `IWorld`. Each frame builds an `AudioSignal` snapshot, updates the camera, renders the active world into a fixed 1280x720 `RenderTarget`, then blits that to the actual window framebuffer (shader cost is independent of window size). Parses `--smoke-test` / `--diagnostic baseline` / `--diagnostic visual` from `args` (passed through from `Program.cs`). All three bounded modes force `Environment.Exit(0)` in `OnUnload` to guarantee the process cannot outlive the window.
- **Worlds** (`Engine/IWorld.cs`, `Worlds/`): a world implements `Load()/Update()/Render()/Unload()`. Only one exists today: `Worlds/World01_StellarNursery/StellarNursery.cs`, a volumetric raymarched nebula shader reacting to two guitar audio channels (Guitar 1 = "Creator": energy/color; Guitar 2 = "Sculptor": density/structure). Now correctly sets the shader's 3D camera basis and `uBassCombined` (see above).
- **Audio pipeline** (`Audio/`): `AudioEngine` captures stereo audio via OpenAL on a background thread, runs an FFT per buffer (MathNet.Numerics), and exposes `Bass`/`Mid`/`Treble`/`Level` per channel, plus a static `IsCapturing` flag used by the perf logger.
- **Live tuning** (`ControlServer.cs`, `Tuning.cs`): a plain `HttpListener` on `http://localhost:8080` serves sliders bound to `Tuning.*` fields for live soundcheck adjustment.
- **Diagnostics** (`Diagnostics/Shaders/`): an isolated debug gradient shader (no uniforms, no dependency on world state) used only by `--diagnostic visual` to prove the shader/quad/uniform pipeline independently of any world.
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
