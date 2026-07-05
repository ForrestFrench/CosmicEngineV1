# IMPLEMENTATION_LOG.md

Chronological log of implementation passes. Kept short — one entry per pass, pointing at commits/AUDIT.md for detail rather than duplicating it.

---

## 2026-07-05 — Baseline Recovery Pass 1

**Executor:** Claude Code / Sonnet

Fixed the runtime shader compile blocker that prevented the app from ever rendering a frame: `stellar_nursery.frag`'s `noise3` function collided with GLSL's deprecated built-in `noise3()`, which returns `vec3` instead of the intended `float`. Renamed to `noise3D` (smallest safe fix, no visual/logic changes).

Also added OpenGL renderer/vendor/version/GLSL-version startup logging (`Engine/CosmicEngine.cs`) and a `[Startup] StellarNursery loaded successfully` smoke-check log (`StellarNursery.cs`), and created `PROJECT_STATE.md` to reflect actual repo state. See `AUDIT.md` Entry 2 for full detail.

Result: `dotnet build` succeeds, `dotnet run` starts and renders continuously. No performance profile, diagnostic suite, or FPS baseline work was in scope for this pass.
