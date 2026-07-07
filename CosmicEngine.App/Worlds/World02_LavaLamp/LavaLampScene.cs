using CosmicEngine.App.Audio;
using CosmicEngine.App.Engine;
using CosmicEngine.App.Rendering;
using OpenTK.Graphics.OpenGL4;
using System;

namespace CosmicEngine.App.Worlds.World02
{
    /// <summary>
    /// World 02: Lava Lamp / Analog Psych (draft v0.1).
    /// A cheap 2D metaball-field fullscreen shader - deliberately much simpler than
    /// Stellar Nursery's volumetric raymarch, to prove the engine supports multiple
    /// worlds and to give a safe, always-visible scene on weak/integrated GPUs.
    /// Guitar 1 (Creator) - color intensity, hue shift, glow/pulse strength.
    /// Guitar 2 (Sculptor) - blob size, distortion/wobble amount and frequency.
    /// All motion/color has a baseline driven by time alone, so the scene is never
    /// black or frozen under silence - audio only modulates on top of that floor.
    /// </summary>
    public class LavaLampScene : IWorld
    {
        private ShaderProgram? _shader;
        private FullscreenQuad? _quad;
        private readonly Camera _camera;

        private float _time;

        private float _sBass1, _sLevel1, _sTreble1;
        private float _sBass2, _sMid2, _sTreble2;
        private const float Smoothing = 0.40f;

        // P1-aware (set by CosmicEngineApp from the active profile): fewer blobs on
        // Safe, a few more on High. Same shader either way - MAX_BLOBS in the GLSL
        // caps the loop, this just picks how many of it actually run.
        public static int BlobCount = 6;

        private static string ShaderPath(string file) =>
            Path.Combine("Worlds", "World02_LavaLamp", "Shaders", file);

        public LavaLampScene(Camera camera)
        {
            _camera = camera;
        }

        public void Load()
        {
            _shader = new ShaderProgram(ShaderPath("lava_lamp.vert"), ShaderPath("lava_lamp.frag"));
            _quad   = new FullscreenQuad();
            Console.WriteLine("[LavaLamp] Loaded.");
        }

        public void Update(float deltaTime, AudioSignal audio)
        {
            _time += deltaTime;

            // Reuse Tuning.cs's floor/max constants (the live-tunable source) rather
            // than duplicating another private copy, unlike StellarNursery.cs's
            // existing (known, documented) duplication.
            _sBass1   = Lerp(_sBass1,   Calibrate(audio.Bass1,   Tuning.BassFloor,   Tuning.BassMax),   Smoothing);
            _sLevel1  = Lerp(_sLevel1,  MathF.Min(audio.Level1 * 5f, 1f),                                Smoothing);
            _sTreble1 = Lerp(_sTreble1, Calibrate(audio.Treble1, Tuning.TrebleFloor, Tuning.TrebleMax), Smoothing);

            _sBass2   = Lerp(_sBass2,   Calibrate(audio.Bass2, Tuning.BassFloor,   Tuning.BassMax), Smoothing);
            _sMid2    = Lerp(_sMid2,    Calibrate(audio.Mid2,  Tuning.MidFloor,    Tuning.MidMax),  Smoothing);
            _sTreble2 = Lerp(_sTreble2, Calibrate(audio.Treble2, Tuning.TrebleFloor, Tuning.TrebleMax), Smoothing);
        }

        public void Render()
        {
            if (_shader == null || _quad == null) return;

            GL.Clear(ClearBufferMask.ColorBufferBit);
            _shader.Use();

            _shader.SetFloat("uTime", _time);
            _shader.SetInt("uBlobCount", BlobCount);

            // Guitar 1 (Creator): color intensity, hue shift, glow/pulse strength.
            // Baseline terms (0.65 / 0.55) keep the palette and glow fully visible
            // under silence; audio only lifts them further.
            float colorIntensity = 0.65f + 0.55f * MathF.Max(_sBass1, _sLevel1);
            float hueShift       = _sTreble1 * 0.6f;
            float glowStrength   = 0.55f + 0.55f * MathF.Max(_sBass1, _sLevel1);
            _shader.SetFloat("uColorIntensity", colorIntensity);
            _shader.SetFloat("uHueShift", hueShift);
            _shader.SetFloat("uGlowStrength", glowStrength);

            // Guitar 2 (Sculptor): blob size, distortion/wobble amount + frequency.
            // Baseline distortion (0.12) keeps a subtle wobble present even in silence.
            float blobSize      = 1.0f + 0.5f * _sBass2;
            float distortion    = 0.12f + 0.35f * _sMid2;
            float wobbleFreqMul = 1.0f + 1.5f * _sTreble2;
            _shader.SetFloat("uBlobSize", blobSize);
            _shader.SetFloat("uDistortion", distortion);
            _shader.SetFloat("uWobbleFreqMul", wobbleFreqMul);

            // Combined: overall blob orbit speed - modest baseline, some audio lift.
            float blobSpeed = 1.0f + 0.5f * MathF.Max(_sBass1, _sBass2);
            _shader.SetFloat("uBlobSpeed", blobSpeed);

            _quad.Draw();
        }

        public void Unload()
        {
            _shader?.Dispose();
            _quad?.Dispose();
            Console.WriteLine("[LavaLamp] Unloaded.");
        }

        private static float Lerp(float current, float target, float smoothing) =>
            current * smoothing + target * (1f - smoothing);

        private static float Calibrate(float raw, float floor, float max) =>
            MathF.Min(MathF.Max(raw - floor, 0f) / max, 1f);
    }
}
