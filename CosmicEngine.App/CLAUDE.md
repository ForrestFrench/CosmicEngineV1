# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Cosmic Engine — a real-time, audio-reactive visual engine for live music performances (see `../Vision`). It listens to two guitar inputs via an audio interface, does FFT analysis, and drives GLSL fragment shaders ("worlds") that are projected/displayed live. There is no offline/batch mode — the intended runtime experience is a fullscreen visual synced to a live audio signal.

## Commands

Run from this directory (`CosmicEngine.App/`, which contains the `.csproj`; the `.sln` lives one level up):

```bash
dotnet build          # build
dotnet run             # run — opens a window, starts audio capture, and starts the control server
dotnet run -- --smoke-test          # bounded ~8s run, prints an fps/frame-time summary, exits cleanly
dotnet run -- --diagnostic baseline # same, plus writes DiagnosticReports/Baseline_<timestamp>/REPORT.md
dotnet run -- --diagnostic visual   # bounded 3-phase visible-frame check (solid color / gradient / world), writes screenshots + REPORT.md
dotnet run -- --profile Safe --smoke-test   # P1: run at a named performance profile (Safe/Balanced/High, default High) - any bounded mode above accepts --profile
dotnet run -- --diagnostic perf-sweep       # P1: 10 bounded sub-runs per profile (Safe, High), ~2 min total, writes DiagnosticReports/PerfSweep_<timestamp>/raw_results.csv + summary.txt
dotnet run -- --world LavaLamp --smoke-test # pick a world (StellarNursery/LavaLamp, default StellarNursery) - any bounded mode above, and perf-sweep, accepts --world; unknown name prints a warning and falls back to StellarNursery rather than crashing
./run-show.sh                               # Scene Dashboard v0.1: unbounded "show mode" - dotnet run -- --profile Safe in the background, then auto-opens http://localhost:8080 (or prints the URL if auto-open fails)
```

There is no test project and no lint config in this repo.

**Do not leave Cosmic Engine running after diagnostics or tests.** Prefer bounded commands such as `dotnet run -- --smoke-test` or `dotnet run -- --diagnostic baseline`/`--diagnostic visual`. If normal `dotnet run` is used, run it only for a bounded manual check and explicitly stop/close the process before reporting completion — it does not exit on its own (see `Engine/CosmicEngine.cs`).

## Operating rules (governance)

These rules were formalized after the 2026-07-06 Fable strategy review (see `AUDIT.md` Entry 12 and
`ROADMAP.md`) in response to repeated open-ended shader-iteration cycles with contested visual payoff.
They apply to every pass, not just visual/shader work:

1. Do not leave Cosmic Engine running after diagnostics or tests.
2. Prefer bounded runs: `--smoke-test`, `--diagnostic baseline`, or specific diagnostic commands.
3. Do not push to GitHub without explicit user approval.
4. Do not self-sign `AUDIT.md` reviewer sign-off — the implementation model never signs its own audit entry.
5. No visual pass is accepted without screenshots.
6. No performance claim is accepted without FPS/frame-time evidence — and evidence means repeated runs (a distribution), not a single reading.
7. No hardware-viability claim is accepted without running on the actual target hardware.
8. Do not accept "non-black pixels" (or any similarly weak proxy metric) as visual success.
9. Commit by intent whenever possible — one concern per commit, not large mixed commits.
10. Do not let shader-art debugging continue indefinitely. If a visual pass fails acceptance twice, or exceeds one agent-day, stop and escalate to the user/ChatGPT with a written options memo instead of continuing to iterate.
11. Infrastructure passes (RenderScale, diagnostics, profiles, etc.) must not include shader-art changes.
12. Shader-art passes must not include unrelated infrastructure changes.
13. If visual payoff is weak relative to the shader complexity being added, stop and escalate rather than adding more procedural noise.
14. Use bounded profile commands for performance testing (`--profile <name> --smoke-test`/`--diagnostic baseline`, or `--diagnostic perf-sweep` for repeated-run evidence) — see `AUDIT.md` Entry 13 (P1) for why single-run readings on this project have repeatedly produced misleading "stable" numbers that hid real variance.
15. Do not make a performance or profile-comparison claim from a single run of any profile — P1's own sweep showed one profile's single-run FPS can land anywhere in a 3x range depending on the run; always cite a run count.
16. Profile/infrastructure passes (RenderScale, diagnostics, perf-sweep, etc.) must not include shader-art changes — see rule 11.

Note: shader files are loaded from disk at runtime via relative paths (e.g. `Worlds/World01_StellarNursery/Shaders/...`), not copied/embedded by the build. Always run `dotnet run` with this directory as the working directory, otherwise shader loading will fail with `FileNotFoundException`.

## Runtime dependencies

- **Audio input**: `Audio/AudioEngine.cs` opens an OpenAL capture device (stereo, 44.1kHz) and expects a real two-channel guitar interface (originally tuned for a Focusrite Clarett — see `Tuning.cs` comment). Running without a capture device attached will throw at startup (`AudioEngine: could not open capture device`).
- **GPU/OpenGL**: rendering is via OpenTK/OpenGL 4. `Rendering/ShaderProgram.cs` throws on shader compile/link failure with the GL info log inlined in the exception message — read that message first when a shader doesn't build.

## Architecture

**Engine loop** (`Engine/CosmicEngine.cs`, class `CosmicEngineApp`): owns the OpenTK `GameWindow`, the `Camera`, and the single active `IWorld`. Each frame: build an `AudioSignal` snapshot from `AudioEngine` → `_camera.Update()` → world renders into a fixed 1280x720 `RenderTarget` → that target is blitted (scaled) to the actual window framebuffer. This means shader cost is constant regardless of window/display size, and resizing the window is purely a blit-scale operation, not a re-render.

**Worlds** (`Engine/IWorld.cs` + `Worlds/`): a "world" is a self-contained visual scene implementing `Load()/Update()/Render()/Unload()`. Adding a new world means implementing `IWorld` and registering it in `Engine/SceneRegistry.cs` (a `SceneDefinition` with Id/DisplayName/Description/Status/DefaultProfile/Showable/Factory) — `Engine/WorldSelector.cs` (`ValidNames`/`Create()`, used by `--world` CLI parsing) is a thin wrapper over `SceneRegistry`, so registering a scene there is enough for both the CLI and the dashboard to pick it up; nothing else in the engine needs to change. Two worlds exist:
- `Worlds/World01_StellarNursery/StellarNursery.cs` (namespace `CosmicEngine.App.Worlds.World01`, folder `World01_StellarNursery` — naming isn't 1:1, keep this in mind when searching) — the default, a volumetric raymarched nebula.
- `Worlds/World02_LavaLamp/LavaLampScene.cs` (namespace `CosmicEngine.App.Worlds.World02`) — a cheap analytic 2D metaball-field scene, added as a multi-world architecture proof (see `AUDIT.md` Entry 14).

Each world owns its own `ShaderProgram`, keeps its own smoothed/calibrated copies of the audio signal, and pushes them to the shader as uniforms every frame. `--world <name>` (see Commands above) selects which world `CosmicEngineApp` constructs via `WorldSelector.Create(name, camera)`; the default (no flag) is `StellarNursery`, unchanged from before world-selection existed.

**Audio pipeline** (`Audio/`): `AudioEngine.CaptureLoop()` runs on a dedicated background thread, does an FFT per buffer (MathNet.Numerics), and exposes two `GuitarChannel`s (`Guitar1`/`Guitar2`, `volatile` fields) with `Level`/`Bass`/`Mid`/`Treble`. `AudioSignal` (in `Audio/AudioSignal.cs`) is the per-frame immutable snapshot passed into `IWorld.Update()`. By convention across worlds: **Guitar 1 = "Creator"** (energy/color/ignition), **Guitar 2 = "Sculptor"** (gravity/structure/motion) — this is a semantic convention, not enforced in code, so preserve it if you add a world.

**Calibration / tuning**: raw FFT band energies are not directly usable — each world applies a floor/max calibration (`Calibrate()` in `StellarNursery.cs`: `(raw - floor) / max`, clamped 0–1) before sending to the shader, then smooths with an exponential lerp. Baseline calibration constants live in `Tuning.cs` (static, mutable at runtime) and are also duplicated as world-local constants in `StellarNursery.cs` — `Tuning.cs` values are the ones live-adjustable via the control server; the world-local constants are its own copy used elsewhere in `Update()`. Don't assume these two sets are kept in sync automatically.

**Live control panel + Scene Dashboard** (`ControlServer.cs`): a plain `HttpListener` on `http://localhost:8080` (no framework) serving a single self-contained HTML page. Two sections: (1) the original audio-tuning sliders bound to `Tuning.*` fields via `POST /set`/`GET /values` — if you add a new tunable, add it to `Tuning.cs`, the `switch` in `ControlServer.Handle`, the `/values` serializer, and the HTML slider markup; (2) the Scene Dashboard v0.1 section — `GET /status` (current world/profile/fps/render target/audio status, read from `CosmicEngineApp.Current`), `GET /scenes` (from `SceneRegistry.All`), `POST /launch` `{sceneId, profile}`, `POST /restart`, `POST /quit`. Launch/restart/quit calls just set thread-safe volatile fields on `CosmicEngineApp` (`RequestSwitch`/`RequestRestart`/`RequestQuit`) from ControlServer's background HTTP thread; the actual world/profile switch happens once per frame in `CosmicEngineApp.OnRenderFrame` → `ApplyPendingSwitch()`, on the render thread, since that's the only thread allowed to touch GL resources. This is **live, in-process** scene and profile switching (no restart) — see `AUDIT.md` Entry 15. Dashboard-driven requests are inert during any bounded `--smoke-test`/`--diagnostic` run.

**Camera** (`Engine/Camera.cs`): a slow, layered sine/cosine drift + zoom (three superimposed frequencies each axis) applied independently of audio, giving worlds a continuous ambient motion; worlds read `Camera.Zoom`/`Camera.Offset` and pass them into shaders as uniforms rather than transforming geometry directly (there is no geometry — everything is fullscreen-quad fragment shader work). Note this 2D drift/zoom camera is separate from — and currently unused by — `stellar_nursery.frag`'s own 3D raymarch camera basis (`uCamPos`/`uCamForward`/`uCamRight`/`uCamUp`), which `StellarNursery.cs` sets directly with a fixed placement.

**Runtime diagnostics** (`Engine/CosmicEngine.cs`): `Program.cs` passes `args` into `CosmicEngineApp.Run(args)`, parsed by `ParseArgs`. Three bounded modes exist beyond normal `dotnet run`: `--smoke-test` (fixed-duration run + fps summary), `--diagnostic baseline` (same, plus a `REPORT.md`), and `--diagnostic visual` (cycles solid-color → debug-gradient-shader → normal world, capturing a screenshot and luminance reading per phase — see `Diagnostics/Shaders/` for the isolated debug shader used in the gradient phase). All three force `Environment.Exit(0)` in `OnUnload` as a deliberate belt-and-suspenders measure so bounded runs never leave the process alive.
