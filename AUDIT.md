# AUDIT.md

Governance/audit log for CosmicEngineV1. Each entry is a point-in-time snapshot of what's accepted, what's in flight, and what's unverified. The implementation model (Claude Code) may author entries but must not sign off on its own work — reviewer sign-off is left blank until a human or a separate reviewer model fills it in.

---

## Entry 1 — Phase 0 baseline

**Date:** 2026-07-05
**Author:** Claude Code (implementation engineer)
**Reviewer sign-off:** _(blank — pending review)_

### Governance docs present
- `CosmicEngine.App/CLAUDE.md`
- `AUDIT.md` (this file, newly created)
- No `PROJECT_STATE.md`, `ROADMAP.md`, or `IMPLEMENTATION_LOG.md` exist yet.

### Current accepted systems
Landed in commit `951bfee` ("Implement Cosmic Engine core"), with dead-code/backup cleanup in `99f40dc`:
- Engine core / game loop (`Engine/CosmicEngine.cs`, `Engine/IWorld.cs`, `Engine/Time.cs`)
- Camera drift system (`Engine/Camera.cs`)
- Audio capture + FFT pipeline (`Audio/AudioEngine.cs`, `Audio/AudioSignal.cs`)
- Render pipeline: fixed-resolution render target + blit-to-screen (`Rendering/RenderTarget.cs`, `Rendering/FullscreenQuad.cs`, `Rendering/ShaderProgram.cs`)
- Live tuning HTTP control server (`ControlServer.cs`, `Tuning.cs`)
- First world: Stellar Nursery (`Worlds/World01_StellarNursery/StellarNursery.cs` + its shaders)

### Candidate/in-progress systems
None currently in flight.

### Known unverified items
- Real guitar hardware input has not been re-verified since commit `951bfee` landed — the audio capture path has not been confirmed against a live two-channel signal post-commit.
- The tuning/calibration duplication between `Tuning.cs` and world-local constants in `StellarNursery.cs` (documented in `CLAUDE.md`) is still unresolved — the two are not guaranteed to stay in sync.

### Known hardware not yet tested
Per `CLAUDE.md`'s Runtime Dependencies section:
- **Audio interface**: OpenAL stereo capture device with a real two-channel guitar input (originally tuned for a Focusrite Clarett) — not yet re-verified against physical hardware since `951bfee`.
- **GPU/OpenGL**: OpenTK/OpenGL 4 rendering path — not yet re-verified on target display/performance hardware since `951bfee`.
