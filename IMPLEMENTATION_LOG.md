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
