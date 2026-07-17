# IMPLEMENTATION_ROADMAP.md — Video Atoms: Sequencing & Migration Strategy (rev 2)

**Rev 2 (post-ChatGPT review):** Phase 1 collapsed to a single proof-of-concept objective; `IVideoDecoder`
abstraction mandated (no engine dependency on ffmpeg); milestones re-verified dependency-clean, independently
testable, engine-always-working; no "big bang" phases. Artistic authority: **COSMIC_ENGINE_PHILOSOPHY.md** —
every pass brief cites it. Per-milestone specs: MILESTONE_BREAKDOWN.md (rev 2).

---

## 1. Guiding strategy (unchanged in substance, tightened in scope)

1. **Strangler, not rewrite.** Every capability arrives as a new registered `IWorld` beside existing scenes;
   the engine loop, registry, dashboard, profiles, calibration, seeds, and diagnostics adopt it unchanged.
   Existing scenes never gain a video dependency; deleting the video stack must never break anything else.
2. **Prove, then build.** Phase 1 is deliberately a spike with one question: *is hybrid video + procedural
   artistically and technically viable on this engine?* The ecosystem (library, director, effects, tagging)
   is built only after that answer is yes — and the answer belongs to the user at the Phase-1 gate.
3. **Abstraction at the one true seam.** The engine depends on `IVideoDecoder`, never on ffmpeg. The first
   implementation is an ffmpeg subprocess pipe; replacing it later (hardware decode, another library) must
   touch only the implementation class and its registration.
4. **Real assets from day one.** `VisionBoard/` already contains the user's curated reference MP4s — Phase 1
   composites one of them with an existing world immediately. No synthetic placeholders.
5. **Infrastructure and art never mix in one pass** (rules 11/12); commits are small, logically grouped, and
   phase-scoped — with line-level staging around the standing uncommitted Cosmic Reef hunks in shared files,
   which must never be touched or committed by video-atom work (Entry 48 disposition remains the user's open
   decision).
6. **Hardware honesty** (rule 7) — **revised sequencing (explicit user direction, Phase 3 pass):** the
   OptiPlex has never run the engine, and Phase 2 (executing the Phase-1 runbook on the physical box) remains
   the first time it does. But Phase 2 is no longer a blocking prerequisite for further Mac-side development —
   Mac development may continue through Phase 4, 5, and beyond without waiting for Phase 2 to complete.
   OptiPlex testing is reframed as **a hardware gate before live-deployment/stage readiness**, not a blocker
   for all further Mac-side development: every major rendering feature built on the Mac is provisional/dev-only
   until it has been profiled on the OptiPlex, but building it does not have to wait for that profiling to
   happen first. No stage-readiness or "this will run live" claim is made before Phase 6 (formal
   OptiPlex/stage validation) runs there for real — that part of rule 7 is unchanged.
7. **Stop-and-document rule.** Any design decision that would require restructuring existing systems halts
   the pass and produces a written issue for architect review instead of a guess.

## 2. Ordered plan

| Phase | Name | Type | Gate after? |
|---|---|---|---|
| 1 | Hybrid Proof (IVideoDecoder + ffmpeg impl + VideoTexture + HybridTest scene + blend slider) | Spike/infra | **Primary architectural gate** (ChatGPT + user; artistic verdict is the user's) |
| 2 | OptiPlex first-light (user executes Phase-1 runbook on the box) | Validation (hardware gate, not a Mac-dev blocker — see §1 rule 6) | Feeds the Phase-1 gate; does not block Phase 3+ |
| 3 | Effect stack v1 + manual controls | Infra | Evidence review |
| 4 | Clip library + JSON sidecars + ingestion CLI (no database) | Offline infra | Schema sign-off |
| 5 | Scene Director v1 (rule-based, seeded) | Infra | **Gate** (user judges show quality) |
| 6 | Formal OptiPlex/stage validation | Validation | User go/no-go |
| 7 | AI tagging + deferred polish | Enhancement | Per-pass review |

Parallelism: Phase 4 may run alongside Phase 3 (offline, disjoint files). **Revised (explicit user direction,
Phase 3 pass): Mac-side development on Phases 3, 4, 5, and beyond may also proceed in parallel with Phase 2**
rather than waiting for it — Phase 2 is a hardware-readiness gate that must clear before anything built this
way is treated as stage-ready (Phase 6), not a gate on writing/building the features themselves. Every phase
still ends with the engine building clean, all existing scenes passing their smoke tests, and a working tree
whose new commits are phase-scoped only.

## 3. What each implementation pass receives

A self-contained house-style brief: exact file-scope allowlist + do-not-touch list (always including the
Cosmic Reef hunks and all existing scene internals); the philosophy doc as the artistic acceptance authority;
required bounded tests + ≥5-run perf distributions; required full-composite screenshots with exact filenames
(the Entry-47 lesson — isolated renders are never visibility evidence); evidence package + zip + `pbcopy`;
doc updates (AUDIT entry with blank sign-off, IMPLEMENTATION_LOG, PROJECT_STATE, ROADMAP, CLAUDE.md when
architectural); commit rules (small, phase-scoped, line-level staged; never push); rule-10 escalation; and
citations of the exact existing code patterns to mirror (AudioEngine's capture thread; ControlServer slider
wiring; CalibrationPresetStore JSON persistence; SceneRegistry world registration; RenderTarget usage).

## 4. Risks to the sequence

- **Entry 48 stays undecided:** manageable — video work stages around those hunks — but every shared-file
  commit costs extra care; the standing recommendation remains that the user disposition Cosmic Reef soon.
- **ffmpeg absence/version drift:** Phase 1 detects and reports loudly with install guidance for both
  machines (HttpListener-fix messaging precedent); bundling is a later decision if ever needed.
- **OptiPlex surprises:** contained by design — Phase 2 exists precisely to surface them while only a spike
  has been built on top.
- **Scope creep in Phase 1:** the brief hard-forbids the ecosystem items (databases, ingestion, metadata,
  director, random selection, semantic transitions, effect stacks). One clip, one child world, one blend
  uniform. Anything more is a later phase.
