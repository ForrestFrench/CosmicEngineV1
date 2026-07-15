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

        private static string ShaderPath(string file) =>
            System.IO.Path.Combine("Worlds", "World04_Underwater", "Shaders", file);

        public UnderwaterScene(Camera camera)
        {
            _camera = camera;
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
    }
}
