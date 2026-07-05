# PROJECT_STATE.md

Point-in-time snapshot of the actual repo state. Update this file when the state changes materially — do not let it drift into aspirational territory.

**Last updated:** 2026-07-05 (Runtime Diagnostics Phase 1 — reviewed and ACCEPTED by ChatGPT, see `AUDIT.md` Entry 3)

## Branch / status

- **Branch:** `cosmicos`
- **Working tree:** clean as of commit `8719a03` (Baseline Recovery Pass 1, reviewed and ACCEPTED by ChatGPT — see `AUDIT.md` Entry 2).
- **In-flight changes (this pass, reviewed and accepted, not yet committed):** `[Perf]` runtime logging, `--smoke-test` and `--diagnostic baseline` CLI modes, `AudioEngine.IsCapturing` accessor, `.gitignore` entry for generated `DiagnosticReports/`, plus this file, an `AUDIT.md` update, and `IMPLEMENTATION_LOG.md`.

## Build / run status

- `dotnet build`: succeeds, 0 warnings, 0 errors.
- `dotnet run`: starts, logs OpenGL renderer/vendor/version, loads `StellarNursery`, and renders continuously. Once/second `[Perf]` line reports fps, frame time, world, window size, render-target size, and audio capture status.
- `dotnet run -- --smoke-test`: runs for a fixed 8s window, prints an average-fps/frame-time summary, then exits cleanly (exit code 0).
- `dotnet run -- --diagnostic baseline`: same bounded run, plus writes `DiagnosticReports/Baseline_<timestamp>/REPORT.md` with OpenGL info, world loaded, average fps/frame time, and known limitations.

## Architecture summary

- **Engine loop** (`Engine/CosmicEngine.cs`): owns the OpenTK window, the `Camera`, and the single active `IWorld`. Each frame builds an `AudioSignal` snapshot, updates the camera, renders the active world into a fixed 1280x720 `RenderTarget`, then blits that to the actual window framebuffer (shader cost is independent of window size). It also now parses `--smoke-test` / `--diagnostic baseline` from `args` (passed through from `Program.cs`) and runs a lightweight per-second perf logger (`RunDiagnostics`).
- **Worlds** (`Engine/IWorld.cs`, `Worlds/`): a world implements `Load()/Update()/Render()/Unload()`. Only one exists today: `Worlds/World01_StellarNursery/StellarNursery.cs`, a volumetric raymarched nebula shader reacting to two guitar audio channels (Guitar 1 = "Creator": energy/color; Guitar 2 = "Sculptor": density/structure).
- **Audio pipeline** (`Audio/`): `AudioEngine` captures stereo audio via OpenAL on a background thread, runs an FFT per buffer (MathNet.Numerics), and exposes `Bass`/`Mid`/`Treble`/`Level` per channel, plus a static `IsCapturing` flag used by the perf logger.
- **Live tuning** (`ControlServer.cs`, `Tuning.cs`): a plain `HttpListener` on `http://localhost:8080` serves sliders bound to `Tuning.*` fields for live soundcheck adjustment.
- **Render resolution and raymarch step count are hardcoded** — 1280x720 in `CosmicEngine.cs`, 16 march steps in the shader. No dynamic scaling exists yet.

## Known limitations

- **No RenderScale / performance-profile system yet.** Render resolution and shader march/shadow step counts are still fixed constants, not configurable. Out of scope for Runtime Diagnostics Phase 1.
- **Diagnostic tooling is minimal, not a full suite.** `--diagnostic baseline` writes one `REPORT.md` with a single short (~8s) sample window — no screenshots, no multi-scenario sweep, no historical comparison.
- **FPS baseline is now measurable but not yet recorded as an accepted number.** The `[Perf]` log and smoke-test summary give a live reading (observed ~48-50 fps on the current dev machine, Intel Iris Graphics 6100), but no target/pass-fail threshold has been agreed yet.
- **No physical OptiPlex validation yet.** All runtime verification so far has run on the current dev machine only; target deployment hardware has not been validated.
- **Real guitar hardware input validation pending.** `Tuning.cs` documents calibration for a Focusrite Clarett interface; more recent references (outside this repo's code) mention a Scarlett 2i2 — whichever is the current physical interface, live two-channel guitar signal has not been re-verified against this build. The `[Perf]` log's `audio: capturing` field only confirms the capture device opened, not that a real guitar signal is present.
- **Tuning/calibration duplication is still unresolved.** `Tuning.cs` holds the live-tunable (control-server-backed) calibration constants; `StellarNursery.cs` keeps its own separate copy of floor/max constants used elsewhere in `Update()`. The two are not kept in sync automatically — documented in `CLAUDE.md`.
- **Dead top-level `Shaders/` folder has been removed** (commit `99f40dc`) — no longer a concern.
