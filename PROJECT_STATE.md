# PROJECT_STATE.md

Point-in-time snapshot of the actual repo state. Update this file when the state changes materially — do not let it drift into aspirational territory.

**Last updated:** 2026-07-05 (Baseline Recovery Pass 1 — reviewed and ACCEPTED by ChatGPT, see `AUDIT.md` Entry 2)

## Branch / status

- **Branch:** `cosmicos`
- **Working tree (pre-Pass-1):** clean
- **In-flight changes (this pass, reviewed and accepted, not yet committed):** shader fix in `Worlds/World01_StellarNursery/Shaders/stellar_nursery.frag`, OpenGL info logging in `Engine/CosmicEngine.cs`, startup smoke-check log in `Worlds/World01_StellarNursery/StellarNursery.cs`, plus this file, an `AUDIT.md` update, and `IMPLEMENTATION_LOG.md`/`ROADMAP.md`.

## Build / run status

- `dotnet build`: succeeds, 0 warnings, 0 errors.
- `dotnet run`: **previously crashed at startup** — `stellar_nursery.frag` failed GLSL compilation (`noise3` collided with GLSL's built-in deprecated `noise3()` function, which returns `vec3`, causing a redeclaration error and a downstream `float`/`vec3` type mismatch). **Fixed in Baseline Recovery Pass 1** by renaming the user-defined function to `noise3D`. App now starts, logs OpenGL renderer/vendor/version, loads `StellarNursery`, and renders continuously.

## Architecture summary

- **Engine loop** (`Engine/CosmicEngine.cs`): owns the OpenTK window, the `Camera`, and the single active `IWorld`. Each frame builds an `AudioSignal` snapshot, updates the camera, renders the active world into a fixed 1280x720 `RenderTarget`, then blits that to the actual window framebuffer (shader cost is independent of window size).
- **Worlds** (`Engine/IWorld.cs`, `Worlds/`): a world implements `Load()/Update()/Render()/Unload()`. Only one exists today: `Worlds/World01_StellarNursery/StellarNursery.cs`, a volumetric raymarched nebula shader reacting to two guitar audio channels (Guitar 1 = "Creator": energy/color; Guitar 2 = "Sculptor": density/structure).
- **Audio pipeline** (`Audio/`): `AudioEngine` captures stereo audio via OpenAL on a background thread, runs an FFT per buffer (MathNet.Numerics), and exposes `Bass`/`Mid`/`Treble`/`Level` per channel.
- **Live tuning** (`ControlServer.cs`, `Tuning.cs`): a plain `HttpListener` on `http://localhost:8080` serves sliders bound to `Tuning.*` fields for live soundcheck adjustment.
- **Render resolution and raymarch step count are hardcoded** — 1280x720 in `CosmicEngine.cs`, 16 march steps in the shader. No dynamic scaling exists yet.

## Known limitations

- **No RenderScale / performance-profile system yet.** Render resolution and shader march/shadow step counts are fixed constants, not configurable.
- **No diagnostic-suite runner yet.** `Program.cs` takes no arguments; there is no `--diagnostic-suite` mechanism, no screenshot capture, no `REPORT.md` generation.
- **No FPS baseline yet.** No FPS measurement or logging exists in the codebase.
- **No physical OptiPlex validation yet.** This pass's runtime verification ran on the current dev machine only (Intel Iris Graphics 6100, OpenGL 4.1); target deployment hardware has not been validated.
- **Real guitar hardware input validation pending.** `Tuning.cs` documents calibration for a Focusrite Clarett interface; more recent references (outside this repo's code) mention a Scarlett 2i2 — whichever is the current physical interface, live two-channel guitar signal has not been re-verified against this build.
- **Tuning/calibration duplication is still unresolved.** `Tuning.cs` holds the live-tunable (control-server-backed) calibration constants; `StellarNursery.cs` keeps its own separate copy of floor/max constants used elsewhere in `Update()`. The two are not kept in sync automatically — documented in `CLAUDE.md`.
- **Dead top-level `Shaders/` folder has been removed** (commit `99f40dc`) — no longer a concern.
