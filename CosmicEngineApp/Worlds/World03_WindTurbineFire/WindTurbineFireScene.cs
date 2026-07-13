using CosmicEngine.App.Audio;
using CosmicEngine.App.Engine;
using CosmicEngine.App.Rendering;
using OpenTK.Graphics.OpenGL4;
using System;

namespace CosmicEngine.App.Worlds.World03
{
    /// <summary>
    /// World 03: Wind Turbine / Fire (Phase 1 visual prototype, v0.1).
    /// Shader-only, fullscreen-quad scene - same architecture as LavaLampScene, no
    /// mesh pipeline, no Blender, no new engine infrastructure. A dark, hazy
    /// dusk-to-night industrial tableau: wind-turbine silhouettes turning against
    /// layered smoke, with an ember-glow horizon that slowly builds with sustained
    /// musical energy. Deliberately cold/desaturated at rest so heat reads as a
    /// genuine "heat against cold" contrast, not orange-on-orange.
    ///
    /// Guitar 1 (Creator) - fire intensity / glow brightness / ember density
    /// (this is intentionally the reverse of a naive "A=wind" mapping - Creator
    /// drives ignition/energy per this project's documented convention).
    /// Guitar 2 (Sculptor) - wind speed / rotor speed / smoke turbulence.
    ///
    /// uSceneHeat is a single continuous 0-1 accumulator (not a state machine) that
    /// integrates fire-drive over time - reaching full "hot" after roughly 3-5
    /// minutes of sustained loud play - and decays slowly (~0.01/s) during quiet
    /// passages, so silence visibly relaxes the scene instead of ratcheting
    /// monotonically. Any "Calm/Ignition/Burn" language in comments below refers to
    /// tuning ranges within that one float, not separate code paths.
    ///
    /// Turbine rotation and smoke drift each have a time-only baseline so the scene
    /// is alive under total silence - audio only ever lifts those values above that
    /// floor, never gates them to black/frozen. Embers are a deliberate exception
    /// (Phase 1.2, per explicit user feedback): they have no baseline and are gated
    /// hard by fire drive/heat, so they are absent at rest and only appear once the
    /// fire signal visibly lights up - grouped with, and timed to, the horizon glow
    /// rather than an always-on layer.
    /// </summary>
    public class WindTurbineFireScene : IWorld
    {
        private ShaderProgram? _shader;
        private FullscreenQuad? _quad;
        private readonly Camera _camera;

        private float _time;

        // Smoothed raw-audio-derived fields (Tuning.cs floor/max, same pattern as
        // LavaLampScene.cs). _1 = Guitar 1 / Creator (fire), _2 = Guitar 2 /
        // Sculptor (wind).
        private float _sBass1, _sLevel1, _sTreble1;
        private float _sBass2, _sMid2, _sTreble2;
        private const float Smoothing = 0.40f;

        // Continuous 0-1 heat accumulator - see class doc comment. Rise rate is
        // derived every frame from Tuning.WindTurbineFireEvolutionSeconds (Phase
        // 1.1: dashboard-adjustable, default 240s/4min - matches the original
        // hardcoded Phase 1 value) so sustained fireDrive == 1.0 reaches full
        // heat in that many seconds; decay is a fixed ~0.01/s regardless of
        // fireDrive, so quiet passages always visibly cool the scene rather than
        // only ever climbing.
        private float _sceneHeat;
        private const float MinEvolutionSeconds = 30f;
        private const float MaxEvolutionSeconds = 300f;
        private const float HeatDecayPerSecond = 0.01f;
        private const float HeatQuietThreshold = 0.15f;

        // Defensive clamp applied every frame - the dashboard slider itself is
        // built with min=30/max=300, but this guards the actual sim against any
        // out-of-range value reaching Tuning.WindTurbineFireEvolutionSeconds by
        // another path (e.g. a future /set caller, a bad manual edit).
        private static float HeatRisePerSecondAtFullDrive =>
            1f / Math.Clamp(Tuning.WindTurbineFireEvolutionSeconds, MinEvolutionSeconds, MaxEvolutionSeconds);

        // Rotor angles integrated in C# (not in the shader) so each turbine can
        // have a distinct phase offset and speed multiplier without any per-turbine
        // uniform arrays - just four floats. FG = foreground, BG = background/hazed.
        private float _rotorAngleFG1 = 0.00f;
        private float _rotorAngleFG2 = 2.10f;
        private float _rotorAngleBG1 = 4.40f;
        private float _rotorAngleBG2 = 1.30f;
        private const float BaseRotorSpeed = 0.35f;   // rad/s, idle baseline under silence
        private const float RotorWindGain  = 1.10f;   // additional rad/s at full wind drive
        private const float FG1SpeedMul = 1.00f, FG2SpeedMul = 1.18f;
        private const float BG1SpeedMul = 0.82f, BG2SpeedMul = 0.94f;

        // Profile-scaled ember count (Safe/High set by CosmicEngineApp, mirrors
        // LavaLampScene.BlobCount) - MAX_EMBERS in the GLSL caps the fixed loop,
        // this just picks how many of it actually run.
        public static int EmberCount = 12;

        private static string ShaderPath(string file) =>
            System.IO.Path.Combine("Worlds", "World03_WindTurbineFire", "Shaders", file);

        public WindTurbineFireScene(Camera camera)
        {
            _camera = camera;
        }

        public void Load()
        {
            _shader = new ShaderProgram(ShaderPath("wind_turbine_fire.vert"), ShaderPath("wind_turbine_fire.frag"));
            _quad   = new FullscreenQuad();
            Console.WriteLine("[WindTurbineFire] Loaded.");
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

            // Calibrated Audio Reactivity Integration v0.1 pattern (AUDIT.md Entry
            // 27, also used by StellarNursery.cs/LavaLampScene.cs): a modest
            // additive nudge from the Calibration tab's post-curve outputs on top
            // of (not instead of) the raw-audio-derived values above. Input A
            // (Creator) feeds fire/glow/embers; Input B (Sculptor) feeds
            // wind/rotor/turbulence - both 0 (no change) whenever calibration is
            // silent or unused.
            float calibratedA = CalibrationEngine.InputA.CurveOutput;
            float calibratedB = CalibrationEngine.InputB.CurveOutput;
            _sBass1  = MathF.Min(_sBass1  + calibratedA * CalibrationEngine.CalibratedBlendWeight, 1f);
            _sLevel1 = MathF.Min(_sLevel1 + calibratedA * CalibrationEngine.CalibratedBlendWeight, 1f);
            _sBass2  = MathF.Min(_sBass2  + calibratedB * CalibrationEngine.CalibratedBlendWeight, 1f);
            _sMid2   = MathF.Min(_sMid2   + calibratedB * CalibrationEngine.CalibratedBlendWeight, 1f);

            float fireDrive = MathF.Max(_sBass1, _sLevel1);
            float windDrive = MathF.Max(_sBass2, _sMid2);

            // uSceneHeat: single continuous float integrator, not a state machine.
            // Above the quiet threshold, heat climbs proportional to fireDrive;
            // below it, heat always decays at a fixed rate, so total silence
            // reliably relaxes the scene back toward cold/calm instead of only
            // ever ratcheting upward.
            if (fireDrive > HeatQuietThreshold)
                _sceneHeat = MathF.Min(_sceneHeat + fireDrive * HeatRisePerSecondAtFullDrive * deltaTime, 1f);
            else
                _sceneHeat = MathF.Max(_sceneHeat - HeatDecayPerSecond * deltaTime, 0f);

            // Rotor integration: baseline idle speed under silence, windDrive adds
            // on top. Distinct per-turbine multipliers (set in field initializers)
            // keep all four turbines visibly out of sync with each other.
            float rotorExtra = windDrive * RotorWindGain;
            _rotorAngleFG1 += (BaseRotorSpeed + rotorExtra) * FG1SpeedMul * deltaTime;
            _rotorAngleFG2 += (BaseRotorSpeed + rotorExtra) * FG2SpeedMul * deltaTime;
            _rotorAngleBG1 += (BaseRotorSpeed + rotorExtra) * BG1SpeedMul * deltaTime;
            _rotorAngleBG2 += (BaseRotorSpeed + rotorExtra) * BG2SpeedMul * deltaTime;
        }

        public void Render()
        {
            if (_shader == null || _quad == null) return;

            GL.Clear(ClearBufferMask.ColorBufferBit);
            _shader.Use();

            _shader.SetFloat("uTime", _time);
            _shader.SetFloat("uSceneHeat", _sceneHeat);
            _shader.SetInt("uEmberCount", EmberCount);

            float fireDrive = MathF.Max(_sBass1, _sLevel1);
            float windDrive = MathF.Max(_sBass2, _sMid2);
            // Baseline (0.30) keeps smoke visibly turbulent (never laminar/static)
            // even under silence; treble2 (transient/pick attack energy) adds gust.
            float smokeTurbulence = 0.30f + 0.55f * _sTreble2 + 0.15f * windDrive;

            _shader.SetFloat("uFireDrive", fireDrive);
            _shader.SetFloat("uWindDrive", windDrive);
            _shader.SetFloat("uSmokeTurbulence", MathF.Min(smokeTurbulence, 1.2f));

            _shader.SetFloat("uRotorAngleFG1", _rotorAngleFG1);
            _shader.SetFloat("uRotorAngleFG2", _rotorAngleFG2);
            _shader.SetFloat("uRotorAngleBG1", _rotorAngleBG1);
            _shader.SetFloat("uRotorAngleBG2", _rotorAngleBG2);

            _quad.Draw();
        }

        public void Unload()
        {
            _shader?.Dispose();
            _quad?.Dispose();
            Console.WriteLine("[WindTurbineFire] Unloaded.");
        }

        private static float Lerp(float current, float target, float smoothing) =>
            current * smoothing + target * (1f - smoothing);

        private static float Calibrate(float raw, float floor, float max) =>
            MathF.Min(MathF.Max(raw - floor, 0f) / max, 1f);
    }
}
