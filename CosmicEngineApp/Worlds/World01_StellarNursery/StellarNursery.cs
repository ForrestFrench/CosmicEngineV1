using CosmicEngine.App.Audio;
using CosmicEngine.App.Engine;
using CosmicEngine.App.Rendering;
using OpenTK.Graphics.OpenGL4;
using System;

namespace CosmicEngine.App.Worlds.World01
{
    /// <summary>
    /// World 01: Stellar Nursery.
    /// Guitar 1 (Creator) - energy, color, ignition.
    /// Guitar 2 (Sculptor) - gravity, structure, motion.
    /// </summary>
    public class StellarNursery : IWorld
    {
        private ShaderProgram? _shader;
        private FullscreenQuad? _quad;
        private readonly Camera _camera;
        private readonly Random _rng = new();

        private float _time;
        private float _seed;

        // Stellar Nursery Showability Consistency Fix: exposes the actual seed in use
        // so diagnostic report writers (CosmicEngine.cs) can log it explicitly instead
        // of leaving it ambiguous - the prior rejected package's t1/t5/t15 captures
        // used a silently-random pool seed with no record of which one, while a
        // separately-captured reference frame used an explicit different seed; the two
        // were then incorrectly compared as if they were the same frame.
        public float Seed => _seed;

        // World memory - state that accumulates across the full performance
        private float _perfTime;       // total seconds since show started
        private float _cumEnergy1;     // Guitar 1 total energy (normalised 0-1 at ~1hr)
        private float _cumEnergy2;     // Guitar 2 total energy (normalised 0-1 at ~1hr)

        // Smoothed audio values
        private float _sBass1, _sMid1, _sTreble1, _sLevel1;
        private float _sBass2, _sMid2, _sTreble2, _sLevel2;
        private const float Smoothing = 0.40f;

        private const float BassFloor   = 0.08f;
        private const float MidFloor    = 0.004f;
        private const float TrebleFloor = 0.001f;
        private const float BassMax     = 35f;
        private const float MidMax      = 12f;
        private const float TrebleMax   = 0.4f;

        private static string ShaderPath(string file) =>
            Path.Combine("Worlds", "World01_StellarNursery", "Shaders", file);

        // Stellar Nursery Showability Audit: nebulaDensity()'s threshold sits at a
        // narrow point relative to the dominant (~1000 ly-scale) density octave, so
        // with a fixed camera + fixed 400 ly march range, whether a given uSeed's
        // large-scale value lands near that threshold (visible cloud structure) or
        // far from it (density saturates to ~0 or ~1 across the whole frustum -> a
        // flat, featureless gradient, no stars/structure visible) is essentially a
        // coin flip. Fully fixing this needs a deeper density/threshold rework (out
        // of scope for a minimal fix - see AUDIT.md). Minimal mitigation: pick the
        // per-launch seed from a small pool of seeds already confirmed (by
        // screenshot) to produce visible structure, instead of an unconstrained
        // random value, so "a new pattern every launch" no longer risks landing on
        // a flat/empty one.
        //
        // Stellar Nursery Showability Revision: the original pool (400, 33, 610, 5)
        // was curated against the old, much smaller compositionOffset (-55,30,0) in
        // stellar_nursery.frag. That offset barely relocated which part of the
        // density field the frustum samples (tiny relative to the dominant octave's
        // ~1000 ly scale), so which seeds looked good was decoupled from where in
        // the FRAME their structure landed - several of them read as structure
        // clustered at the corners/edges with an empty center. The offset was
        // changed to (0,-400,0) - large enough to meaningfully relocate the sampled
        // region - which invalidated the old pool (a shifted offset is effectively
        // a different sample point per seed) and required re-curating from scratch:
        // sampled 20+ seeds against the new offset, verified each candidate at full
        // resolution (not just a thumbnail - a first thumbnail-only pass was
        // actively misleading).
        //
        // Also caught during re-curation: raising the density field's time-evolution
        // rate (see stellar_nursery.frag's tScale, Showability Audit) means a seed
        // that looks good at t=0 can drift toward a flat/empty state within 15s, the
        // same threshold-crossing fragility playing out over time instead of across
        // seeds. Two initial candidates (480, 88) looked good at t=1s but visibly
        // flattened out by t=15s and were dropped. The final pool was verified at
        // BOTH t=1s and t=15s (not just launch) via --diagnostic motion.
        private static readonly float[] KnownGoodSeeds = { 777f, 33f, 61f, 155f };

        public StellarNursery(Camera camera)
        {
            _camera = camera;
        }

        // Diagnostic-only: set COSMICENGINE_SEED to pin uSeed to a known value so
        // --diagnostic visual captures are reproducible instead of landing on a
        // random (sometimes near-empty) region of the procedural density field.
        // Normal play is unaffected unless this env var is explicitly set.
        public static int DebugMode = 0; // 0 = normal, 1 = density/opacity debug, 2 = radiance debug

        public void Load()
        {
            _shader = new ShaderProgram(ShaderPath("stellar_nursery.vert"),
                                        ShaderPath("stellar_nursery.frag"));
            _quad   = new FullscreenQuad();

            string? seedOverride = Environment.GetEnvironmentVariable("COSMICENGINE_SEED");
            if (seedOverride != null && float.TryParse(seedOverride, out float forcedSeed))
            {
                _seed = forcedSeed;
                Console.WriteLine($"[StellarNursery] Loaded. Seed: {_seed:F2} (COSMICENGINE_SEED override)");
            }
            else
            {
                _seed = KnownGoodSeeds[_rng.Next(KnownGoodSeeds.Length)];
                Console.WriteLine($"[StellarNursery] Loaded. Seed: {_seed:F2} (picked from known-good pool)");
            }

            Console.WriteLine("[Startup] StellarNursery loaded successfully");
        }

        public void Update(float deltaTime, AudioSignal audio)
        {
            _time += deltaTime;

            _sBass1   = Lerp(_sBass1,   audio.Bass1,   Smoothing);
            _sMid1    = Lerp(_sMid1,    audio.Mid1,    Smoothing);
            _sTreble1 = Lerp(_sTreble1, audio.Treble1, Smoothing);
            _sLevel1  = Lerp(_sLevel1,  audio.Level1,  Smoothing);

            _sBass2   = Lerp(_sBass2,   audio.Bass2,   Smoothing);
            _sMid2    = Lerp(_sMid2,    audio.Mid2,    Smoothing);
            _sTreble2 = Lerp(_sTreble2, audio.Treble2, Smoothing);
            _sLevel2  = Lerp(_sLevel2,  audio.Level2,  Smoothing);

            // World memory: accumulate performance history
            _perfTime   += deltaTime;
            _cumEnergy1 += MathF.Max(audio.Bass1, audio.Level1) * deltaTime;
            _cumEnergy2 += MathF.Max(audio.Bass2, audio.Level2) * deltaTime;
        }

        public void Render()
        {
            if (_shader == null || _quad == null) return;

            GL.Clear(ClearBufferMask.ColorBufferBit);
            _shader.Use();

            _shader.SetFloat("uTime", _time);
            _shader.SetFloat("uSeed", _seed);

            // Camera (legacy 2D drift/zoom - unused by this shader, see uCam* below)
            _shader.SetFloat("uZoom",   _camera.Zoom);
            _shader.SetFloat("uDriftX", _camera.Offset.X);
            _shader.SetFloat("uDriftY", _camera.Offset.Y);

            // 3D camera basis the raymarcher actually uses. Fixed placement ~200 ly
            // out looking back toward the origin, per the shader's documented setup.
            // (Baseline Recovery Pass 2: these were never set, so they defaulted to
            // vec3(0), making rayDir a 0/0 NaN and the whole scene render black.)
            _shader.SetVector3("uCamPos",     0f, 0f, -200f);
            _shader.SetVector3("uCamForward", 0f, 0f, 1f);
            _shader.SetVector3("uCamRight",   1f, 0f, 0f);
            _shader.SetVector3("uCamUp",      0f, 1f, 0f);

            // World memory - normalised so 1.0 represents ~1 hour of performance
            _shader.SetFloat("uPerfTime",   _perfTime);
            _shader.SetFloat("uCumEnergy1", MathF.Min(_cumEnergy1 / 180f, 1f));
            _shader.SetFloat("uCumEnergy2", MathF.Min(_cumEnergy2 / 180f, 1f));

            // Guitar 1 - Creator
            float bass1 = Calibrate(_sBass1, BassFloor, BassMax);
            // Calibrated Audio Reactivity Integration v0.1: a modest additive nudge
            // from the Calibration tab's Input A post-curve output on top of the
            // existing raw-FFT-derived bass1 value, not a replacement of it - see
            // the class doc comment above and AUDIT.md Entry 27 for why. Input A's
            // curve is shaped by the user (Linear/Sensitive/Compressed/S-Curve or a
            // custom drag), so this is where a smoother, less on/off response comes
            // from; bass1 alone is unchanged (this term is 0) when no scene is using
            // calibration or the calibrated output is silent, preserving the
            // existing recovered art direction exactly as before this pass.
            bass1 = MathF.Min(bass1 + CalibrationEngine.InputA.CurveOutput * CalibrationEngine.CalibratedBlendWeight, 1f);
            _shader.SetFloat("uBass1",   bass1);
            _shader.SetFloat("uMid1",    Calibrate(_sMid1,    MidFloor,    MidMax));
            _shader.SetFloat("uTreble1", Calibrate(_sTreble1, TrebleFloor, TrebleMax));
            _shader.SetFloat("uLevel1",  MathF.Min(_sLevel1 * 5f, 1f));

            // Guitar 2 - Sculptor
            float bass2 = Calibrate(_sBass2, BassFloor, BassMax);
            // Same modest additive treatment for Input B, feeding the density-field
            // threshold/extinction terms (nebulaDensity()'s g2bass) that already
            // tolerate bass2's full 0-1 raw swing today (the Entry 16-18 seed pool
            // was curated against that existing range) - this addition is capped
            // well inside that already-tested range, not a new risk to seed 777's
            // known-good structure.
            bass2 = MathF.Min(bass2 + CalibrationEngine.InputB.CurveOutput * CalibrationEngine.CalibratedBlendWeight, 1f);
            _shader.SetFloat("uBass2",   bass2);
            _shader.SetFloat("uMid2",    Calibrate(_sMid2,    MidFloor,    MidMax));
            _shader.SetFloat("uTreble2", Calibrate(_sTreble2, TrebleFloor, TrebleMax));
            _shader.SetFloat("uLevel2",  MathF.Min(_sLevel2 * 5f, 1f));

            // uBassCombined was also never set (defaulted to 0) - drives the shader's
            // final brightness envelope alongside uDimLevel.
            _shader.SetFloat("uBassCombined",   MathF.Max(bass1, bass2));
            _shader.SetFloat("uDimLevel",       Tuning.DimLevel);
            _shader.SetFloat("uBassBrightness", Tuning.BassBrightness);

            // Diagnostic-only debug view (Visual Recovery Pass 1): 0 in all normal
            // play/capture, only ever non-zero for the --diagnostic visual
            // DensityDebug/RadianceDebug phases (see Engine/CosmicEngine.cs).
            _shader.SetInt("uDebugMode", DebugMode);

            _quad.Draw();
        }

        public void Unload()
        {
            _shader?.Dispose();
            _quad?.Dispose();
            Console.WriteLine("[StellarNursery] Unloaded.");
        }

        private static float Lerp(float current, float target, float smoothing) =>
            current * smoothing + target * (1f - smoothing);

        private static float Calibrate(float raw, float floor, float max) =>
            MathF.Min(MathF.Max(raw - floor, 0f) / max, 1f);
    }
}
