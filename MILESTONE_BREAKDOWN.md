# MILESTONE_BREAKDOWN.md — Video Atoms: Per-Milestone Specifications (rev 2)

**Rev 2 changes (post-ChatGPT review):** Phase 1 simplified to a single proof-of-concept objective per the
architect's direction (old M1/M2/M3 collapsed); `IVideoDecoder` abstraction made a hard requirement so the
engine never depends on ffmpeg directly; all milestones re-checked so none depends on a system that doesn't
exist yet, each is independently testable, and each leaves the engine in a working state. All phases must be
consistent with **COSMIC_ENGINE_PHILOSOPHY.md** (the artistic north star) — pass briefs cite it.

House rules apply to every phase: bounded runs, ≥5-run perf distributions, full-composite screenshot evidence,
evidence package + AUDIT.md entry (blank sign-off), small logically-grouped commits scoped strictly to the
phase (line-level staging where shared files carry unrelated uncommitted work), no push without explicit
approval, rule-10 two-failure escalation, stop-and-document instead of guessing on any decision that would
require major restructuring.

**Standing repo note:** the Cosmic Reef Phase 1 pivot remains uncommitted with an open options memo (AUDIT
Entry 48, failed 60fps gate) awaiting the user's disposition choice. Video-atom phases proceed by explicit
user direction, must never touch or commit those hunks, and must use line-level staging when adding to the
shared files that carry them (`SceneRegistry.cs`, `CosmicEngine.cs`, `ControlServer.cs`).

---

## Phase 1 — Hybrid Proof (single objective: prove hybrid video + procedural is artistically and technically viable)

- **Objective:** one working, measurable, reviewable demonstration — a real clip from `VisionBoard/` rendered
  as a texture with an existing, unmodified Cosmic Engine world composited over/behind it, blend adjustable,
  audio reactivity intact. Nothing else.
- **Explicitly ignored by design (architect's instruction):** databases of any kind (incl. SQLite), AI
  ingestion, automatic metadata, Scene Director, random clip selection, semantic transitions, effect stacks,
  clip libraries, loop crossfades. No additional architecture unless the proof literally cannot run without it.
- **Required work:**
  - `IVideoDecoder` interface (`Rendering/Video/`): `Open(path)`, frame properties (size/fps/duration),
    `TryAcquireFrame(...)` non-blocking, `Dispose()`. **The engine references only this interface.**
  - `FfmpegPipeDecoder : IVideoDecoder` — first implementation: ffmpeg subprocess, raw-BGRA pipe, background
    reader thread (mirror `AudioEngine.CaptureLoop`'s idiom), 3-frame ring buffer, hold-last-frame on stall,
    loud actionable error if ffmpeg is missing, guaranteed child-process cleanup on every exit path.
  - `VideoTexture` — GL texture wrapper with ≤1 `glTexSubImage2D` upload per render frame.
  - `HybridTestScene : IWorld` — registered (`Id: "HybridTest"`, Showable), hardcoded clip path (one
    `VisionBoard/` file), hosts one existing registered world as a child (default: StellarNursery, the
    volumetric renderer; child selectable via existing `--world`-style arg parsing if trivial) rendered to a
    private `RenderTarget`, composited by a minimal shader: `mix(video, child, uBlend)` — one blend uniform,
    no blend modes, no effects.
  - Blend control: one `Tuning.HybridBlend` slider via the established Tuning/ControlServer pattern
    (append-only additions; line-level staging around the uncommitted Cosmic Reef hunks in `ControlServer.cs`).
  - Perf measurement on the Mac mini + a written, step-by-step OptiPlex measurement runbook (the OptiPlex is
    not reachable from the dev machine — see Phase 2).
- **Acceptance criteria:** all eight architect-stated objectives evidenced: (1) existing scenes render
  unchanged (smoke tests + shared-file regression per the scoped-regression rule); (2) hardcoded local video
  loads; (3) video displays as a texture (motion-diagnostic frame-advance proof); (4) the volumetric child
  world renders over/behind the video at operator-chosen blend; (5) existing scene-state "bloom"/arc behavior
  functions unchanged in the child (documented honestly: the engine has no post-process bloom — this
  criterion maps to child-scene internal behavior, verified via World04-as-child or Stellar Nursery
  equivalents); (6) audio reactivity functions inside the composite (calibration test-pulse evidence);
  (7) blend adjustable live from the dashboard; (8) Mac mini perf distributions recorded + OptiPlex runbook
  delivered. Zero orphan ffmpeg/engine processes across quit, world-switch, and kill paths.
- **Performance considerations:** ≥5-run tables — HybridTest (Safe child/High composite and High/High) vs
  the existing-world baselines; decode-thread CPU%; explicit statement that ≥60fps High on Mac mini holds, or
  honest disclosure + options if not. No OptiPlex claims (rule 7) — measurements there are Phase 2.
- **Dependencies:** none beyond the existing engine + system ffmpeg + `VisionBoard/` footage (verified present).
- **Required review before proceeding:** **the primary architectural gate.** ChatGPT + user review of the
  full review package (PHASE1_SUMMARY / ARCHITECTURE_CHANGES / PERFORMANCE_NOTES / TESTING_RESULTS /
  KNOWN_LIMITATIONS + evidence). The artistic-viability judgment (does the composite feel like Cosmic Engine
  per the philosophy doc, or like a media player with an overlay?) belongs to the user, not the implementer.

## Phase 2 — OptiPlex first-light (user-executed hardware checkpoint)
- **Objective:** first-ever engine run + Phase-1 hybrid measurement on the actual Dell OptiPlex 5070 Micro.
- **Required work:** execute the Phase-1 runbook on the box: record OS/driver/GL capabilities, install .NET 8
  + ffmpeg, run bounded smoke tests for all worlds + HybridTest (Safe and High), record distributions.
- **Acceptance criteria:** a written capability/fps report — whatever the numbers are; this checkpoint cannot
  "fail," only inform. If the box won't run the engine, that finding is the deliverable.
- **Performance considerations:** converts all OptiPlex conjecture to data; informs every later phase.
- **Dependencies:** Phase 1 built; physical access to the OptiPlex (user).
- **Required review:** findings folded into the Phase-1 gate decision.

## Phase 3 — Effect stack v1 + manual performance controls
- **Objective:** the lightweight effect vocabulary (grayscale, mirror X/Y, lift/gamma/gain grade, vignette,
  playback speed) + dashboard controls, on top of the proven Phase-1 composite.
- **Required work:** uniform-driven effects in the composite shader; ClipPlayer-level speed pacing;
  ControlServer endpoints + minimal dashboard section (existing slider-wiring pattern); optional
  audio-reactive hooks via existing calibrated signals (Creator→grade/energy, Sculptor→motion/speed).
- **Acceptance criteria:** each effect evidenced on/off in full-composite screenshots; controls verified via
  HTTP transcript; philosophy compliance (effects serve mood; no gimmick stacking); fps budget held.
- **Dependencies:** Phase 1 only.
- **Required review:** standard evidence review.

## Phase 4 — Clip library, metadata sidecars, ingestion CLI
- **Objective:** clips become a curated, queryable library. **JSON sidecars + generated index only — no
  SQLite, no database** (follows the existing `CalibrationPresetStore` pattern; a database is not introduced
  unless a future phase demonstrates the index is insufficient).
- **Required work:** finalize `clip.json` schema + `library.json` index; ingestion CLI (ffmpeg normalize to
  ≤720p intra-only, sidecar skeleton, idempotent, checksums); `.gitignore` (binaries out, sidecars in);
  hand-tag 5-10 `VisionBoard/` clips across ≥3 motifs from the philosophy doc; `HybridTestScene` (or its
  successor) loads by clip id.
- **Acceptance criteria:** raw file → playing id-addressable clip round-trip; corrupt/missing sidecar handled
  loudly-but-non-fatally; schema reviewed by ChatGPT (it is the long-term contract, including for future AI
  tagging).
- **Dependencies:** Phase 1 runtime. May run in parallel with Phase 3 (offline, disjoint files).
- **Required review:** schema sign-off.

## Phase 5 — Scene Director v1 (rule-based, seeded)
- **Objective:** curated-random clip selection + timed transitions driven by theme preset, song state, and
  seeded randomness (`COSMICENGINE_SEED` convention: reproducible with a seed, varied without).
- **Required work:** director-level song-state integrator (existing accumulator pattern); filter→weight→
  seeded-pick selection with a no-repeat window; 2-slot crossfade transitions; dashboard theme preset +
  current/next/skip/hold; every decision logged for review.
- **Acceptance criteria:** same seed+inputs ⇒ identical sequence; different seeds ⇒ different shows;
  selections track energy-state changes (test-pulse evidence); pop-free transitions in motion captures;
  transitions justified by arc/music per the philosophy doc (no random cuts).
- **Dependencies:** Phases 3 + 4.
- **Required review:** **gate** — user judges show quality; outcome scopes Phase 7.

## Phase 6 — Formal OptiPlex/stage validation
- **Objective:** honest stage-readiness determination on real hardware with real inputs (rule 7 discharged).
- **Required work:** full perf-sweep + show-length bounded runs of the director-driven scene on the OptiPlex
  with the Scarlett; thermal observation; box-specific profile tuning if needed; operator runbook.
- **Acceptance criteria:** distributions at show length; explicit go/no-go per profile; recovery paths
  demonstrated on the box.
- **Dependencies:** Phases 2 + 5.
- **Required review:** user go/no-go — the only sign-off that counts.

## Phase 7 — AI-assisted tagging + deferred polish (only as justified by the Phase-5 gate)
- **Objective:** reduce curation labor; finish deferred niceties that earned their place.
- **Required work (menu, scoped at the gate):** offline AI tagger writing the *same* sidecar schema (human
  review before library inclusion; never in the render path); LUT grading; reverse/pingpong; richer
  transitions; >2-layer composites if a real show need emerged; the long-standing conditional Blender spike.
- **Dependencies:** Phase 5 gate outcomes.
- **Required review:** standard per-pass evidence review.
