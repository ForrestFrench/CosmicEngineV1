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

        public StellarNursery(Camera camera)
        {
            _camera = camera;
        }

        public void Load()
        {
            _shader = new ShaderProgram(ShaderPath("stellar_nursery.vert"),
                                        ShaderPath("stellar_nursery.frag"));
            _quad   = new FullscreenQuad();
            _seed   = (float)(_rng.NextDouble() * 1000.0);

            Console.WriteLine($"[StellarNursery] Loaded. Seed: {_seed:F2}");
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
            _shader.SetFloat("uBass1",   bass1);
            _shader.SetFloat("uMid1",    Calibrate(_sMid1,    MidFloor,    MidMax));
            _shader.SetFloat("uTreble1", Calibrate(_sTreble1, TrebleFloor, TrebleMax));
            _shader.SetFloat("uLevel1",  MathF.Min(_sLevel1 * 5f, 1f));

            // Guitar 2 - Sculptor
            float bass2 = Calibrate(_sBass2, BassFloor, BassMax);
            _shader.SetFloat("uBass2",   bass2);
            _shader.SetFloat("uMid2",    Calibrate(_sMid2,    MidFloor,    MidMax));
            _shader.SetFloat("uTreble2", Calibrate(_sTreble2, TrebleFloor, TrebleMax));
            _shader.SetFloat("uLevel2",  MathF.Min(_sLevel2 * 5f, 1f));

            // uBassCombined was also never set (defaulted to 0) - drives the shader's
            // final brightness envelope alongside uDimLevel.
            _shader.SetFloat("uBassCombined",   MathF.Max(bass1, bass2));
            _shader.SetFloat("uDimLevel",       Tuning.DimLevel);
            _shader.SetFloat("uBassBrightness", Tuning.BassBrightness);

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
