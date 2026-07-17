using CosmicEngine.App.Audio;
using CosmicEngine.App.Engine;
using CosmicEngine.App.Rendering;
using OpenTK.Graphics.OpenGL4;
using System;

namespace CosmicEngine.App.Worlds.World04
{
    /// <summary>
    /// World 04: Underwater / Jellyfish / Caustic Light (Phase 1 - atmosphere
    /// prototype only, v0.1). Shader-only, fullscreen-quad scene - same
    /// architecture family as Lava Lamp / Wind Turbine Fire, no mesh pipeline,
    /// no Blender, no new engine infrastructure. A dark, cool, three-zone
    /// water column - dim green-teal light entry near the top, desaturated
    /// slate-blue midwater, near-black blue-violet abyss at the bottom - lit by
    /// slow-swaying analytic god rays, upper-water caustic shimmer, drifting
    /// haze/murk, and marine-snow particulate that visibly glints as it drifts
    /// through the light shafts.
    ///
    /// Phase 1 is deliberately an atmosphere-only pass: no jellyfish, no
    /// tentacles, no silhouettes, no refraction warp. A sparse, hard-gated
    /// bioluminescent-mote shimmer at high bloom is the only forward-looking
    /// hint of Phase 2's jellyfish - it draws no actual creature shapes.
    ///
    /// Guitar 1 (Creator) -> uLightDrive: ray/caustic/glow brightness and
    /// bloom-accumulation drive (this project's documented convention:
    /// Input A = Creator = light/energy). Guitar 2 (Sculptor) -> uCurrentDrive:
    /// water current speed / particle drift; uCurrentTurbulence adds transient
    /// turbulence from treble2, mirroring WindTurbineFireScene's
    /// smokeTurbulence shape.
    ///
    /// _bloom is a single continuous 0-1 accumulator (not a state machine),
    /// copied mechanically from WindTurbineFireScene's _sceneHeat: it
    /// integrates light drive over time - reaching full "bloom" after roughly
    /// Tuning.UnderwaterEvolutionSeconds of sustained play - and decays at a
    /// fixed ~0.01/s during quiet passages, so silence visibly relaxes the
    /// scene instead of ratcheting monotonically.
    ///
    /// Rays/particles/haze each have a time-only baseline so the scene is
    /// alive under total silence - audio only ever lifts those values above
    /// that floor, never gates them to black/frozen. Bioluminescent motes are
    /// the deliberate exception (mirroring Phase 1.2's ember treatment): zero
    /// baseline, hard-gated by _bloom, absent at rest.
    ///
    /// Underwater must read as slower/more languid than Wind Turbine Fire's
    /// fire/wind - smoothing is 0.60 here (vs. WTF's 0.40), and the light path
    /// additionally runs through its own asymmetric attack/release envelope
    /// (moderate attack, ~2-3s release) rather than tracking the raw envelope
    /// directly, so light swells and lingers instead of twitching with every
    /// transient - a deliberate anti-twitchiness design goal, not polish.
    ///
    /// Phase 2 (jellyfish/tentacle pass, v0.1): adds 3 mid-distance jellyfish
    /// forms directly in underwater.frag (SDF bell + continuous ridged-FBM
    /// tentacle filament field - see that file's own header for the full
    /// design). The only new C#-side state is per-jellyfish pulse phase
    /// (_jellyPhase0/1/2 below) - integrated here rather than derived from
    /// uTime*rate in the shader, mirroring WindTurbineFireScene's rotor-angle
    /// integration pattern, because the pulse *rate* itself is audio-reactive
    /// (Input B) and therefore time-varying; a shader-side "uTime * rate"
    /// would not correctly integrate a rate that changes over time (same
    /// class of bug rotor-angle integration already avoids for WTF). Pulse
    /// rate = "B makes it move" (Input B / Sculptor), pulse/rim brightness =
    /// "A makes it glow" (Input A / Creator), per this project's established
    /// convention - both already-existing signals, no new audio plumbing.
    ///
    /// Phase 1 "Living Water" (fresh architect plan, post committed Phase 3):
    /// converts the previously screen-fixed jellyfish into drifting
    /// inhabitants of an evolving environment. Jellyfish position moves from
    /// purely hash-derived (fixed forever) to C#-integrated drift-path state
    /// (_jellyDrift below) for the identical reason pulse phase already is -
    /// the drift rate is current-driven (Input B) and therefore time-varying.
    /// Each jellyfish continuously crosses the visible frame edge-to-edge
    /// over roughly 90-150s, wrapping to a freshly re-hashed depth/vertical-
    /// wander shape each crossing; the 3 jellyfish's crossing phases are
    /// staggered by construction so at most one is ever fully off-frame at
    /// once (see the class-level comment on JellyEdgeX for the arithmetic).
    /// Depth also now drives scale, haze-occlusion, and drift speed (near =
    /// bigger/faster/clearer, far = smaller/slower/hazier - standard
    /// parallax), read by the shader from uJellyDepth0/1/2 instead of a
    /// static per-jellyfish hash. The previously-unused `Camera` (see
    /// `Engine/Camera.cs`) is now actually consumed here - its already-
    /// running Offset is forwarded as uCameraOffsetX/Y and applied as a
    /// per-layer parallax shift in the shader (water/rays/caustics/haze get
    /// smaller multipliers, particles/plankton/jellyfish the full amount) -
    /// see underwater.frag's main() for the parallax wiring. A new plankton
    /// bloom-field layer (uPlanktonCount, profile-scaled like ParticleCount)
    /// and the bloom-arc-band remapping are implemented entirely in
    /// underwater.frag - see that file's header for the full design.
    ///
    /// Phase 4 "Presence / Color / Depth Population" (direct user review
    /// feedback post-Phase 3): adds a distant, abstract "alien presence"
    /// shadow layer, several cheap background jellyfish, and tasteful color-
    /// variation nudges - all implemented in underwater.frag (see that
    /// file's header). The only C#-side addition is BackgroundJellyCount
    /// (profile-scaled, mirrors ParticleCount/PlanktonCount) - the presence
    /// layer and background jellies both use pure shader-side hash + uTime
    /// placement, no new per-instance C#-integrated state.
    /// </summary>
    public class UnderwaterScene : IWorld
    {
        private ShaderProgram? _shader;
        private FullscreenQuad? _quad;
        private readonly Camera _camera;

        private float _time;

        // Smoothed raw-audio-derived fields (Tuning.cs floor/max, same pattern
        // as WindTurbineFireScene.cs). _1 = Guitar 1 / Creator (light), _2 =
        // Guitar 2 / Sculptor (current).
        private float _sBass1, _sLevel1, _sTreble1;
        private float _sBass2, _sMid2, _sTreble2;

        // Slower than WTF's 0.40 - underwater must respond more languidly.
        private const float Smoothing = 0.60f;

        // Asymmetric attack/release envelope applied to lightDrive on top of
        // the smoothing above - moderate attack (light can swell fairly
        // promptly) but a slow ~2-3s release (light lingers instead of
        // dropping the instant the signal does). This envelope's output
        // (_lightEnvelope) is what actually reaches the shader as uLightDrive
        // and is also what feeds the _bloom accumulator below.
        private float _lightEnvelope;
        private const float LightAttackSeconds = 0.50f;
        private const float LightReleaseSeconds = 2.60f;

        // Continuous 0-1 bloom accumulator - mechanics copied directly from
        // WindTurbineFireScene._sceneHeat. Rise rate is derived every frame
        // from Tuning.UnderwaterEvolutionSeconds (dashboard-adjustable,
        // default 240s/4min) so sustained lightDrive == 1.0 reaches full
        // bloom in that many seconds; decay is a fixed ~0.01/s regardless of
        // drive, so quiet passages always visibly relax the scene.
        private float _bloom;
        private const float MinEvolutionSeconds = 30f;
        private const float MaxEvolutionSeconds = 300f;
        private const float BloomDecayPerSecond = 0.01f;
        private const float BloomQuietThreshold = 0.15f;

        // Defensive clamp applied every frame - the dashboard slider itself is
        // built with min=30/max=300, but this guards the actual sim against
        // any out-of-range value reaching Tuning.UnderwaterEvolutionSeconds by
        // another path.
        private static float BloomRisePerSecondAtFullDrive =>
            1f / Math.Clamp(Tuning.UnderwaterEvolutionSeconds, MinEvolutionSeconds, MaxEvolutionSeconds);

        // Profile-scaled particle count (Safe/High set by CosmicEngine.cs,
        // mirrors WindTurbineFireScene.EmberCount) - MAX_PARTICLES in the
        // GLSL caps the fixed loop, this just picks how many of it run.
        public static int ParticleCount = 36;

        // Phase 1 "Living Water": profile-scaled plankton bloom-field count
        // (Safe/High set by CosmicEngine.cs alongside ParticleCount above) -
        // MAX_PLANKTON in the GLSL caps the fixed loop. Distinct from
        // ParticleCount (marine snow, unmodified) - see underwater.frag's
        // plankton section for why a separate, cheaper layer was used.
        public static int PlanktonCount = 60;

        // Phase 4 "Presence / Color / Depth Population": profile-scaled
        // background-jellyfish count (Safe/High set by CosmicEngine.cs
        // alongside ParticleCount/PlanktonCount above) - MAX_BG_JELLY in the
        // GLSL caps the fixed loop. Deliberately no C#-integrated per-
        // instance state (unlike _jellyDrift0/1/2 below) - background
        // jellies are cheap, shader-hash-placed, and don't need audio-
        // reactive lap timing, see underwater.frag's "Background Jellyfish"
        // section for the full rationale.
        public static int BackgroundJellyCount = 6;

        // Phase 2: per-jellyfish pulse phase, continuously wrapping in [0,1).
        // Initial offsets are hand-picked (not hashed) purely so the 3
        // jellyfish start visibly out of sync with each other from frame 1
        // rather than all beginning at phase 0. Per-jellyfish rate
        // multipliers below add further, permanent desync on top of that.
        private float _jellyPhase0 = 0.10f;
        private float _jellyPhase1 = 0.55f;
        private float _jellyPhase2 = 0.82f;
        private const float JellyBasePulseHz  = 0.22f; // ~4.5s cycle at rest
        private const float JellyPulseRateGain = 0.35f; // extra Hz at full Input B drive
        private const float JellyRateMul0 = 1.00f;
        private const float JellyRateMul1 = 0.87f;
        private const float JellyRateMul2 = 1.14f;

        // --- Phase 1 "Living Water": jellyfish drift paths -------------------
        // Per-jellyfish state for the C#-integrated horizontal "lap" crossing
        // plus an independent vertical wander, mirroring the exact reasoning
        // already established for _jellyPhase0/1/2 above: the crossing RATE
        // is current-driven (Input B) and therefore time-varying, so a
        // shader-side uTime*rate would not correctly integrate it.
        //
        // LapPhase sweeps 0->1 across one off-frame-to-off-frame horizontal
        // crossing (JellyEdgeX is well outside the visible frame half-width
        // of ~0.89, so both ends of a lap sit off-frame - the wrap at 1->0 is
        // therefore never visible, not a teleport). Reaching 1.0 re-hashes
        // this jelly's depth (near/far), vertical-wander shape, and next
        // lap's duration from a per-lap seed that increments every wrap, so
        // consecutive laps never repeat identically. YPhase is a fully
        // independent, slower wrap driving vertical wander only.
        //
        // Direction is fixed per jellyfish for the whole session (hand-picked
        // like the pulse-phase offsets, not re-hashed) so a lap's "opposite
        // side" re-entry falls out naturally from the phase wrap alone, with
        // no direction-reversal logic needed.
        //
        // Off-frame-window arithmetic (why "at most one fully off-frame at a
        // time" holds by construction, not luck): each lap's off-frame
        // fraction is 2*(JellyEdgeX - 0.89) / (2*JellyEdgeX) ~= 0.152 of the
        // full lap, centered on this jelly's own LapPhase wrap point (0/1
        // boundary). With crossing-phase offsets staggered 0.05 / 0.40 / 0.75
        // (gaps of 0.35, hand-picked below, same spirit as the pulse-phase
        // offsets), the three ~0.152-wide off-frame windows land at
        // [0.974,1]u[0,0.126], [0.324,0.476], [0.674,0.826] - none overlap,
        // with >=0.12 of headroom at every boundary.
        private const float JellyEdgeX = 1.05f;          // off-frame X bound each lap starts/ends at (visible frame half-width ~0.89)
        private const float JellyBaseLapSeconds = 120f;  // lap-duration range center (90-150s target)
        private const float JellyLapJitterSeconds = 30f; // +/- range around the base, re-hashed every lap
        private const float JellyMinLapSeconds = 70f;    // hard floor so a current spike can't collapse a lap
        private const float JellyMaxLapSeconds = 170f;   // hard ceiling so a quiet current can't stall a lap

        private struct JellyDriftState
        {
            public float LapPhase;   // 0-1, wraps - horizontal crossing progress
            public float YPhase;     // 0-1, wraps - independent vertical wander
            public int   LapSeed;    // increments every lap wrap, feeds the per-lap hash
            public float Direction;  // +1 = drifts left->right, -1 = right->left (fixed per jelly)
            public float Depth;      // 0 (far) .. 1 (near), re-hashed every lap
            public float YCenter;    // this lap's vertical-wander center, re-hashed every lap
            public float YAmp;       // this lap's vertical-wander amplitude, re-hashed every lap
            public float YFreqMul;   // this lap's vertical-wander frequency multiplier, re-hashed every lap
            public float LapSeconds; // this lap's duration, re-hashed every lap (clamped 70-170s)
            public float PosX;       // resolved this frame in UpdateJellyDrift(), read by Render()
            public float PosY;
        }

        private JellyDriftState _jellyDrift0 = new() { LapPhase = 0.05f, YPhase = 0.15f, LapSeed = 101, Direction =  1f };
        private JellyDriftState _jellyDrift1 = new() { LapPhase = 0.40f, YPhase = 0.55f, LapSeed = 202, Direction = -1f };
        private JellyDriftState _jellyDrift2 = new() { LapPhase = 0.75f, YPhase = 0.85f, LapSeed = 303, Direction =  1f };

        private static string ShaderPath(string file) =>
            System.IO.Path.Combine("Worlds", "World04_Underwater", "Shaders", file);

        public UnderwaterScene(Camera camera)
        {
            _camera = camera;

            // Resolve each jelly's first lap (depth/vertical-wander shape/
            // duration) immediately so frame 1 doesn't render with a
            // zeroed-out default Depth/YAmp before the first wrap (which
            // could otherwise be 90-150s away).
            ReseedLap(ref _jellyDrift0, 0);
            ReseedLap(ref _jellyDrift1, 1);
            ReseedLap(ref _jellyDrift2, 2);
        }

        public void Load()
        {
            _shader = new ShaderProgram(ShaderPath("underwater.vert"), ShaderPath("underwater.frag"));
            _quad   = new FullscreenQuad();
            Console.WriteLine("[Underwater] Loaded.");
        }

        public void Update(float deltaTime, AudioSignal audio)
        {
            _time += deltaTime;

            _sBass1   = Lerp(_sBass1,   Calibrate(audio.Bass1,   Tuning.BassFloor,   Tuning.BassMax),   Smoothing);
            _sLevel1  = Lerp(_sLevel1,  MathF.Min(audio.Level1 * 5f, 1f),                                Smoothing);
            _sTreble1 = Lerp(_sTreble1, Calibrate(audio.Treble1, Tuning.TrebleFloor, Tuning.TrebleMax), Smoothing);

            _sBass2   = Lerp(_sBass2,   Calibrate(audio.Bass2, Tuning.BassFloor,   Tuning.BassMax), Smoothing);
            _sMid2    = Lerp(_sMid2,    Calibrate(audio.Mid2,  Tuning.MidFloor,    Tuning.MidMax),  Smoothing);
            _sTreble2 = Lerp(_sTreble2, Calibrate(audio.Treble2, Tuning.TrebleFloor, Tuning.TrebleMax), Smoothing);

            // Calibrated Audio Reactivity Integration v0.1 pattern (AUDIT.md
            // Entry 27, also used by every other world): a modest additive
            // nudge from the Calibration tab's post-curve outputs on top of
            // (not instead of) the raw-audio-derived values above. Input A
            // (Creator) feeds light; Input B (Sculptor) feeds current - both
            // 0 (no change) whenever calibration is silent or unused.
            float calibratedA = CalibrationEngine.InputA.CurveOutput;
            float calibratedB = CalibrationEngine.InputB.CurveOutput;
            _sBass1  = MathF.Min(_sBass1  + calibratedA * CalibrationEngine.CalibratedBlendWeight, 1f);
            _sLevel1 = MathF.Min(_sLevel1 + calibratedA * CalibrationEngine.CalibratedBlendWeight, 1f);
            _sBass2  = MathF.Min(_sBass2  + calibratedB * CalibrationEngine.CalibratedBlendWeight, 1f);
            _sMid2   = MathF.Min(_sMid2   + calibratedB * CalibrationEngine.CalibratedBlendWeight, 1f);

            float lightDriveRaw = MathF.Max(_sBass1, _sLevel1);

            // Asymmetric envelope: moderate attack toward a rising signal,
            // slow (~2-3s) release toward a falling one, so light swells and
            // lingers rather than tracking the raw envelope 1:1.
            float attackRate  = MathF.Min(deltaTime / LightAttackSeconds, 1f);
            float releaseRate = MathF.Min(deltaTime / LightReleaseSeconds, 1f);
            if (lightDriveRaw > _lightEnvelope)
                _lightEnvelope += (lightDriveRaw - _lightEnvelope) * attackRate;
            else
                _lightEnvelope += (lightDriveRaw - _lightEnvelope) * releaseRate;

            // uBloom: single continuous float integrator, not a state
            // machine. Above the quiet threshold, bloom climbs proportional
            // to the enveloped light drive; below it, bloom always decays at
            // a fixed rate, so total silence reliably relaxes the scene.
            if (_lightEnvelope > BloomQuietThreshold)
                _bloom = MathF.Min(_bloom + _lightEnvelope * BloomRisePerSecondAtFullDrive * deltaTime, 1f);
            else
                _bloom = MathF.Max(_bloom - BloomDecayPerSecond * deltaTime, 0f);

            // Phase 2: jellyfish pulse-phase integration. Input B (Sculptor /
            // "water motion") sets the pulse rate - reuses the same
            // current-drive signal Render() derives for uCurrentDrive, so
            // "B makes it move" applies uniformly to current AND pulse rate.
            // Per-jellyfish rate multipliers keep all 3 permanently desynced.
            float jellyPulseDrive = MathF.Max(_sBass2, _sMid2);
            float jellyPulseHz = JellyBasePulseHz + JellyPulseRateGain * jellyPulseDrive;
            _jellyPhase0 = Wrap01(_jellyPhase0 + jellyPulseHz * JellyRateMul0 * deltaTime);
            _jellyPhase1 = Wrap01(_jellyPhase1 + jellyPulseHz * JellyRateMul1 * deltaTime);
            _jellyPhase2 = Wrap01(_jellyPhase2 + jellyPulseHz * JellyRateMul2 * deltaTime);

            // Phase 1 "Living Water": jellyfish drift-path integration. Same
            // current-drive signal as the pulse rate above and Render()'s
            // uCurrentDrive - "B makes it move" now also applies to how fast
            // each jellyfish crosses the frame, not just its pulse rate.
            float driftCurrentDrive = MathF.Max(_sBass2, _sMid2);
            UpdateJellyDrift(ref _jellyDrift0, 0, deltaTime, driftCurrentDrive);
            UpdateJellyDrift(ref _jellyDrift1, 1, deltaTime, driftCurrentDrive);
            UpdateJellyDrift(ref _jellyDrift2, 2, deltaTime, driftCurrentDrive);
        }

        public void Render()
        {
            if (_shader == null || _quad == null) return;

            GL.Clear(ClearBufferMask.ColorBufferBit);
            _shader.Use();

            _shader.SetFloat("uTime", _time);
            _shader.SetFloat("uBloom", _bloom);
            _shader.SetFloat("uLightDrive", _lightEnvelope);
            _shader.SetInt("uParticleCount", ParticleCount);

            float currentDrive = MathF.Max(_sBass2, _sMid2);
            // Baseline (0.30) keeps current visibly turbulent (never
            // laminar/static) even under silence; treble2 (transient/pick
            // attack energy) adds a gust - mirrors WindTurbineFireScene's
            // smokeTurbulence shape exactly.
            float currentTurbulence = 0.30f + 0.55f * _sTreble2 + 0.15f * currentDrive;

            _shader.SetFloat("uCurrentDrive", currentDrive);
            _shader.SetFloat("uCurrentTurbulence", MathF.Min(currentTurbulence, 1.2f));

            _shader.SetFloat("uJellyPhase0", _jellyPhase0);
            _shader.SetFloat("uJellyPhase1", _jellyPhase1);
            _shader.SetFloat("uJellyPhase2", _jellyPhase2);

            // Phase 1 "Living Water": C#-integrated drift position/depth,
            // read directly by the shader instead of deriving position from
            // hash constants (renderJelly() keeps its own hash-seeded
            // variation for bell/tentacle *character* - only *position* and
            // *depth* come from here now). Sent as paired floats rather than
            // a vec2 uniform to avoid adding a new SetVector2 to the shared
            // ShaderProgram.cs (governance rule 12 - this is a shader-art
            // pass and should not carry an unrelated infra change); combined
            // into vec2 on the shader side.
            _shader.SetFloat("uJellyPos0X", _jellyDrift0.PosX);
            _shader.SetFloat("uJellyPos0Y", _jellyDrift0.PosY);
            _shader.SetFloat("uJellyPos1X", _jellyDrift1.PosX);
            _shader.SetFloat("uJellyPos1Y", _jellyDrift1.PosY);
            _shader.SetFloat("uJellyPos2X", _jellyDrift2.PosX);
            _shader.SetFloat("uJellyPos2Y", _jellyDrift2.PosY);
            _shader.SetFloat("uJellyDepth0", _jellyDrift0.Depth);
            _shader.SetFloat("uJellyDepth1", _jellyDrift1.Depth);
            _shader.SetFloat("uJellyDepth2", _jellyDrift2.Depth);

            // Phase 1 "Living Water": the previously-unused Camera (see
            // Engine/Camera.cs - constructed in this scene's constructor but
            // never read until now) is forwarded here as a per-layer
            // parallax shift, wired up entirely in underwater.frag's
            // main(). A small, modest boost from current drive makes the
            // drift read as slightly brisker current rather than a static
            // rate - "slightly increase drift rate with Input B, keep
            // amplitude modest", same currentDrive signal as everything
            // else in this method.
            float cameraDriftBoost = 1.0f + 0.35f * currentDrive;
            _shader.SetFloat("uCameraOffsetX", _camera.Offset.X * cameraDriftBoost);
            _shader.SetFloat("uCameraOffsetY", _camera.Offset.Y * cameraDriftBoost);

            _shader.SetInt("uPlanktonCount", PlanktonCount);

            // Phase 4 "Presence / Color / Depth Population": profile-scaled
            // background-jellyfish count - see underwater.frag's
            // "Background Jellyfish" section. No per-instance uniforms
            // needed (unlike the 3 hero jellies) - all placement/motion is
            // derived shader-side from hash + uTime.
            _shader.SetInt("uBgJellyCount", BackgroundJellyCount);

            _quad.Draw();
        }

        public void Unload()
        {
            _shader?.Dispose();
            _quad?.Dispose();
            Console.WriteLine("[Underwater] Unloaded.");
        }

        private static float Lerp(float current, float target, float smoothing) =>
            current * smoothing + target * (1f - smoothing);

        private static float Wrap01(float v) => v - MathF.Floor(v);

        private static float Calibrate(float raw, float floor, float max) =>
            MathF.Min(MathF.Max(raw - floor, 0f) / max, 1f);

        // Identical formula to underwater.frag's own hash1() - deliberately
        // mirrored so C#-side per-lap hashing reads consistently with the
        // shader's own per-jellyfish hashing convention, even though the two
        // are computed independently (this hash never needs to agree
        // numerically with the shader's - only its *shape*, cheap/well-
        // distributed/deterministic, needs to match).
        private static float Hash1(float n) => Frac(MathF.Sin(n) * 43758.5453123f);

        private static float Frac(float x) => x - MathF.Floor(x);

        private static float Lerp01(float a, float b, float t) => a + (b - a) * t;

        // Re-hashes one jellyfish's per-lap state (depth, vertical-wander
        // shape, lap duration) from a seed combining its per-jellyfish
        // identity (matching underwater.frag's own `seed = idx*41.7+5.0`
        // per-jelly base, so C#/shader hashing stay conceptually aligned)
        // with a per-lap counter, so consecutive laps never look identical.
        private static void ReseedLap(ref JellyDriftState s, int jellyIndex)
        {
            float baseSeed = jellyIndex * 41.7f + 5.0f;
            float h = baseSeed + s.LapSeed * 71.311f;

            s.Depth      = Hash1(h + 1.0f);                                   // 0 (far) .. 1 (near)
            s.YCenter    = Lerp01(-0.05f, 0.20f, Hash1(h + 2.0f));            // matches the old static baseY band
            s.YAmp       = Lerp01(0.025f, 0.075f, Hash1(h + 3.0f));           // gentle, smaller than the X crossing
            s.YFreqMul   = Lerp01(0.6f, 1.4f, Hash1(h + 4.0f));
            float jitter = (Hash1(h + 5.0f) * 2f - 1f) * JellyLapJitterSeconds;
            s.LapSeconds = Math.Clamp(JellyBaseLapSeconds + jitter, JellyMinLapSeconds, JellyMaxLapSeconds);
        }

        // Advances one jellyfish's drift-path state by one frame. Horizontal
        // ("lap") position sweeps linearly across JellyEdgeX at a rate driven
        // by this lap's duration, this jelly's own depth (near = faster
        // apparent crossing, standard parallax), and current drive (Input B,
        // "B makes it move"); wrapping past 1.0 re-hashes the next lap via
        // ReseedLap. Vertical wander is a fully independent, slower sine
        // layered on top, using its own continuously-wrapping phase.
        private static void UpdateJellyDrift(ref JellyDriftState s, int jellyIndex, float deltaTime, float currentDrive)
        {
            float depthSpeedMul   = Lerp01(0.75f, 1.30f, s.Depth); // near = faster
            float currentSpeedMul = 1.0f + 0.5f * currentDrive;
            float lapRate = (1f / MathF.Max(s.LapSeconds, JellyMinLapSeconds)) * depthSpeedMul * currentSpeedMul;

            s.LapPhase += lapRate * deltaTime;
            if (s.LapPhase >= 1f)
            {
                s.LapPhase -= 1f;
                s.LapSeed++;
                ReseedLap(ref s, jellyIndex);
            }

            float yRate = (0.05f + 0.02f * currentDrive) * s.YFreqMul;
            s.YPhase = Wrap01(s.YPhase + yRate * deltaTime);

            s.PosX = s.Direction > 0f
                ? Lerp01(-JellyEdgeX, JellyEdgeX, s.LapPhase)
                : Lerp01(JellyEdgeX, -JellyEdgeX, s.LapPhase);
            s.PosY = s.YCenter + s.YAmp * MathF.Sin(s.YPhase * MathF.Tau);
        }
    }
}
