# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Cosmic Engine — a real-time, audio-reactive visual engine for live music performances (see `../Vision`). It listens to two guitar inputs via an audio interface, does FFT analysis, and drives GLSL fragment shaders ("worlds") that are projected/displayed live. There is no offline/batch mode — the intended runtime experience is a fullscreen visual synced to a live audio signal.

## Commands

Run from this directory (`CosmicEngine.App/`, which contains the `.csproj`; the `.sln` lives one level up):

```bash
dotnet build          # build
dotnet run             # run — opens a window, starts audio capture, and starts the control server
```

There is no test project and no lint config in this repo.

Note: shader files are loaded from disk at runtime via relative paths (e.g. `Worlds/World01_StellarNursery/Shaders/...`), not copied/embedded by the build. Always run `dotnet run` with this directory as the working directory, otherwise shader loading will fail with `FileNotFoundException`.

## Runtime dependencies

- **Audio input**: `Audio/AudioEngine.cs` opens an OpenAL capture device (stereo, 44.1kHz) and expects a real two-channel guitar interface (originally tuned for a Focusrite Clarett — see `Tuning.cs` comment). Running without a capture device attached will throw at startup (`AudioEngine: could not open capture device`).
- **GPU/OpenGL**: rendering is via OpenTK/OpenGL 4. `Rendering/ShaderProgram.cs` throws on shader compile/link failure with the GL info log inlined in the exception message — read that message first when a shader doesn't build.

## Architecture

**Engine loop** (`Engine/CosmicEngine.cs`, class `CosmicEngineApp`): owns the OpenTK `GameWindow`, the `Camera`, and the single active `IWorld`. Each frame: build an `AudioSignal` snapshot from `AudioEngine` → `_camera.Update()` → world renders into a fixed 1280x720 `RenderTarget` → that target is blitted (scaled) to the actual window framebuffer. This means shader cost is constant regardless of window/display size, and resizing the window is purely a blit-scale operation, not a re-render.

**Worlds** (`Engine/IWorld.cs` + `Worlds/`): a "world" is a self-contained visual scene implementing `Load()/Update()/Render()/Unload()`. Adding a new world means implementing `IWorld` and swapping `_activeWorld` in `CosmicEngineApp.Run()` — nothing else in the engine needs to change. Currently only one world exists: `Worlds/World01_StellarNursery/StellarNursery.cs` (namespace is `CosmicEngine.App.Worlds.World01`, folder is `World01_StellarNursery` — naming isn't 1:1, keep this in mind when searching). Each world owns its own `ShaderProgram`, keeps its own smoothed/calibrated copies of the audio signal, and pushes them to the shader as uniforms every frame.

**Audio pipeline** (`Audio/`): `AudioEngine.CaptureLoop()` runs on a dedicated background thread, does an FFT per buffer (MathNet.Numerics), and exposes two `GuitarChannel`s (`Guitar1`/`Guitar2`, `volatile` fields) with `Level`/`Bass`/`Mid`/`Treble`. `AudioSignal` (in `Audio/AudioSignal.cs`) is the per-frame immutable snapshot passed into `IWorld.Update()`. By convention across worlds: **Guitar 1 = "Creator"** (energy/color/ignition), **Guitar 2 = "Sculptor"** (gravity/structure/motion) — this is a semantic convention, not enforced in code, so preserve it if you add a world.

**Calibration / tuning**: raw FFT band energies are not directly usable — each world applies a floor/max calibration (`Calibrate()` in `StellarNursery.cs`: `(raw - floor) / max`, clamped 0–1) before sending to the shader, then smooths with an exponential lerp. Baseline calibration constants live in `Tuning.cs` (static, mutable at runtime) and are also duplicated as world-local constants in `StellarNursery.cs` — `Tuning.cs` values are the ones live-adjustable via the control server; the world-local constants are its own copy used elsewhere in `Update()`. Don't assume these two sets are kept in sync automatically.

**Live control panel** (`ControlServer.cs`): a plain `HttpListener` on `http://localhost:8080` (no framework) serving a single self-contained HTML page with sliders bound to `Tuning.*` fields via `POST /set` and `GET /values`. This is meant to be tweaked live during a performance/soundcheck, separate from a rebuild — if you add a new tunable, add it to `Tuning.cs`, the `switch` in `ControlServer.Handle`, the `/values` serializer, and the HTML slider markup.

**Camera** (`Engine/Camera.cs`): a slow, layered sine/cosine drift + zoom (three superimposed frequencies each axis) applied independently of audio, giving worlds a continuous ambient motion; worlds read `Camera.Zoom`/`Camera.Offset` and pass them into shaders as uniforms rather than transforming geometry directly (there is no geometry — everything is fullscreen-quad fragment shader work).

**Loose files under `Shaders/`** (top-level, not `Worlds/.../Shaders/`) are not referenced from any `.cs` file currently — treat them as inactive/leftover rather than part of the active render path unless you wire them up.
