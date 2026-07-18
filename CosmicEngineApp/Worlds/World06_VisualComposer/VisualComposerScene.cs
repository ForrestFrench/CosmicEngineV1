using CosmicEngine.App.Audio;
using CosmicEngine.App.Engine;
using CosmicEngine.App.Rendering;
using CosmicEngine.App.Rendering.Video;
using CosmicEngine.App.Worlds.World01;
using OpenTK.Graphics.OpenGL4;
using System;
using System.IO;

namespace CosmicEngine.App.Worlds.World06
{
    /// <summary>
    /// World 06: Visual Composer Sandbox. Artistic-exploration pass, explicitly NOT part of the
    /// phased roadmap (no Scene Director, no AI selection, no runtime automation, no clip
    /// library/database). Reuses World05 HybridTest's video/child-world/composite seams
    /// (IVideoDecoder/FfmpegPipeDecoder/VideoTexture, a StellarNursery child rendered into a
    /// private RenderTarget, the Phase 3 effect-stack pattern) and adds two small,
    /// composition-specific inline shader techniques (luminance-seeded motes, an independent
    /// raking light) - see composer.frag. Nine hardcoded compositions, cycled via
    /// Tuning.ComposerIndex from the dashboard, no recompile required to switch between them.
    /// </summary>
    public class VisualComposerScene : IWorld
    {
        public struct Composition
        {
            public string Name;
            public string ClipPath;
            public float TrimStart;
            public float TrimDuration;
            public float DefaultBlend;
            public float TintR, TintG, TintB;
            public int ParticleSeed;
            public bool ParticlesEnabled;
            public float LightRakeAngle;
            public float LightRakeSpeed;
            public bool DissolveAtTail;
            public float VolumetricDensityScale;
        }

        private const string VB = "/Volumes/External Hard Drive FCF/CosmicEngine/VisionBoard/";

        // Nine hardcoded compositions - deliberately different layer recipes per
        // COMPOSITION_NOTES.md, not the same fog-on-video treatment repeated with a new clip.
        public static readonly Composition[] Compositions = new[]
        {
            new Composition { Name="Cosmos (stillness)", ClipPath=VB+"10339-865412856_medium.mp4",
                TrimStart=0.35f, TrimDuration=12.9f, DefaultBlend=0.15f,
                TintR=0.6f, TintG=0.75f, TintB=1.0f, ParticleSeed=1, ParticlesEnabled=true,
                LightRakeAngle=0.4f, LightRakeSpeed=0.015f, DissolveAtTail=false, VolumetricDensityScale=0.5f },

            new Composition { Name="Deep Ocean (storm-breath)", ClipPath=VB+"16160-269534741_medium.mp4",
                TrimStart=5.0f, TrimDuration=17.0f, DefaultBlend=0.35f,
                TintR=0.15f, TintG=0.55f, TintB=0.65f, ParticleSeed=2, ParticlesEnabled=true,
                LightRakeAngle=1.55f, LightRakeSpeed=0.03f, DissolveAtTail=false, VolumetricDensityScale=1.2f },

            new Composition { Name="Fire (generative ignition)", ClipPath=VB+"191325-890894257.mp4",
                TrimStart=0.5f, TrimDuration=18.5f, DefaultBlend=0.5f,
                TintR=1.0f, TintG=0.45f, TintB=0.1f, ParticleSeed=3, ParticlesEnabled=true,
                LightRakeAngle=0.0f, LightRakeSpeed=0.08f, DissolveAtTail=true, VolumetricDensityScale=1.4f },

            new Composition { Name="Humanity (solemn parallax)", ClipPath=VB+"110153-686125612.mp4",
                TrimStart=5.0f, TrimDuration=22.0f, DefaultBlend=0.1f,
                TintR=0.5f, TintG=0.45f, TintB=0.4f, ParticleSeed=4, ParticlesEnabled=false,
                LightRakeAngle=0.78f, LightRakeSpeed=0.01f, DissolveAtTail=false, VolumetricDensityScale=0.25f },

            new Composition { Name="Forests (reverent immensity)", ClipPath=VB+"theredwoods.mp4",
                TrimStart=30.0f, TrimDuration=10.7f, DefaultBlend=0.2f,
                TintR=0.35f, TintG=0.6f, TintB=0.3f, ParticleSeed=5, ParticlesEnabled=true,
                LightRakeAngle=1.2f, LightRakeSpeed=0.02f, DissolveAtTail=false, VolumetricDensityScale=0.4f },

            new Composition { Name="Machinery (archival exploration)", ClipPath=VB+"1971 Aeronautics and Space Highlights~medium.mp4",
                TrimStart=60.0f, TrimDuration=6.1f, DefaultBlend=0.3f,
                TintR=0.6f, TintG=0.6f, TintB=0.65f, ParticleSeed=6, ParticlesEnabled=false,
                LightRakeAngle=2.1f, LightRakeSpeed=0.05f, DissolveAtTail=false, VolumetricDensityScale=0.3f },

            new Composition { Name="Skies (spectral observation)", ClipPath=VB+"abovethehorizon.mp4",
                TrimStart=45.0f, TrimDuration=8.5f, DefaultBlend=0.25f,
                TintR=0.7f, TintG=0.5f, TintB=0.9f, ParticleSeed=7, ParticlesEnabled=true,
                LightRakeAngle=0.9f, LightRakeSpeed=0.012f, DissolveAtTail=false, VolumetricDensityScale=0.35f },

            new Composition { Name="Abstract Textures (restless microscopy)", ClipPath=VB+"disintegrationline.mp4",
                TrimStart=20.0f, TrimDuration=6.7f, DefaultBlend=0.55f,
                TintR=0.8f, TintG=0.8f, TintB=0.85f, ParticleSeed=8, ParticlesEnabled=true,
                LightRakeAngle=3.0f, LightRakeSpeed=0.1f, DissolveAtTail=true, VolumetricDensityScale=0.6f },

            new Composition { Name="Conflict (catastrophic overwhelm)", ClipPath=VB+"184061-872413614_medium.mp4",
                TrimStart=10.0f, TrimDuration=6.4f, DefaultBlend=0.2f,
                TintR=1.0f, TintG=0.2f, TintB=0.1f, ParticleSeed=9, ParticlesEnabled=true,
                LightRakeAngle=1.0f, LightRakeSpeed=0.06f, DissolveAtTail=true, VolumetricDensityScale=1.0f },
        };

        private readonly Camera _camera;
        private readonly Camera _childCamera;

        private IVideoDecoder? _decoder;
        private VideoTexture? _videoTexture;
        private ShaderProgram? _compositeShader;
        private FullscreenQuad? _quad;

        private IWorld? _childWorld;
        private RenderTarget? _childTarget;

        private float _time;
        private float _loopStartTime;
        private bool _decoderFailed;
        private int _loadedIndex = -1;

        private static string ShaderPath(string file) =>
            Path.Combine("Worlds", "World06_VisualComposer", "Shaders", file);

        public VisualComposerScene(Camera camera)
        {
            _camera = camera;
            _childCamera = new Camera();
        }

        public void Load()
        {
            _compositeShader = new ShaderProgram(ShaderPath("composer.vert"), ShaderPath("composer.frag"));
            _quad = new FullscreenQuad();

            _childWorld = new StellarNursery(_childCamera);
            _childWorld.Load();
            _childTarget = new RenderTarget(1280, 720);

            // Evidence-capture convenience only (mirrors the COSMICENGINE_SEED env-var precedent
            // already used elsewhere in this codebase): lets bounded diagnostic runs pick a
            // composition without a running dashboard. Dashboard slider (Tuning.ComposerIndex)
            // remains the live, in-process way to switch once the engine is actually running.
            string? envIdx = Environment.GetEnvironmentVariable("COSMICENGINE_COMPOSER_INDEX");
            if (envIdx != null && int.TryParse(envIdx, out int parsedIdx))
                Tuning.ComposerIndex = parsedIdx;

            LoadComposition(Math.Clamp(Tuning.ComposerIndex, 0, Compositions.Length - 1));

            Console.WriteLine($"[VisualComposer] Loaded. {Compositions.Length} compositions registered.");
        }

        private void LoadComposition(int index)
        {
            if (index == _loadedIndex) return;
            _loadedIndex = index;

            _decoder?.Dispose();
            _videoTexture?.Dispose();
            _decoder = null;
            _videoTexture = null;
            _decoderFailed = false;
            _loopStartTime = _time;

            var comp = Compositions[index];
            Tuning.ComposerBlend = comp.DefaultBlend; // seed the dashboard slider with this composition's tuned default; still live-adjustable afterward
            try
            {
                _decoder = new FfmpegPipeDecoder();
                _decoder.Open(comp.ClipPath, comp.TrimStart, comp.TrimDuration);
                _videoTexture = new VideoTexture(_decoder);
            }
            catch (Exception ex)
            {
                _decoderFailed = true;
                Console.WriteLine($"[VisualComposer] Composition '{comp.Name}': decoder failed to open (scene continues without video layer): {ex.Message}");
            }

            Console.WriteLine($"[VisualComposer] Switched to composition {index}: '{comp.Name}', decoderOk={!_decoderFailed}.");
        }

        public void Update(float deltaTime, AudioSignal audio)
        {
            _time += deltaTime;

            int wanted = Math.Clamp(Tuning.ComposerIndex, 0, Compositions.Length - 1);
            if (wanted != _loadedIndex) LoadComposition(wanted);

            _childCamera.Update(deltaTime, audio.Bass1);
            _childWorld?.Update(deltaTime, audio);

            if (_decoder != null)
                _decoder.PlaybackSpeed = Tuning.ComposerPlaybackSpeed;

            _videoTexture?.Update();
        }

        public void Render()
        {
            if (_compositeShader == null || _quad == null || _childWorld == null || _childTarget == null)
                return;

            var comp = Compositions[_loadedIndex];

            GL.GetInteger(GetPName.FramebufferBinding, out int outerFbo);

            _childTarget.Bind();
            _childWorld.Render();

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, outerFbo);
            GL.Viewport(0, 0, 1280, 720);

            // Additive calibrated-audio nudge, same convention as every other world: Creator
            // (Guitar 1 / InputA) drives light/energy, Sculptor (Guitar 2 / InputB) drives
            // motion/structure. Here both feed the same visible "energy" scalar for the
            // particle/light layers, capped and scaled by ComposerAudioReactivity.
            float audioNudge = Math.Clamp(
                (CalibrationEngine.InputA.CurveOutput + CalibrationEngine.InputB.CurveOutput) * 0.5f
                * Math.Clamp(Tuning.ComposerAudioReactivity, 0f, 2f), 0f, 1.5f);

            float loopDur = Math.Max(comp.TrimDuration, 0.5f);
            float loopPhase = ((_time - _loopStartTime) % loopDur) / loopDur;

            _compositeShader.Use();
            _compositeShader.SetFloat("uTime", _time);
            _compositeShader.SetFloat("uBlend", Math.Clamp(Tuning.ComposerBlend, 0f, 1f));
            _compositeShader.SetVector3("uTint", comp.TintR, comp.TintG, comp.TintB);
            _compositeShader.SetFloat("uParticleSeed", comp.ParticleSeed * 13.37f);
            _compositeShader.SetInt("uParticlesEnabled", comp.ParticlesEnabled ? 1 : 0);
            _compositeShader.SetFloat("uParticleDensity", Math.Clamp(Tuning.ComposerParticleDensity, 0f, 2f));
            _compositeShader.SetFloat("uLightRakeAngle", comp.LightRakeAngle);
            _compositeShader.SetFloat("uLightRakeSpeed", comp.LightRakeSpeed);
            _compositeShader.SetFloat("uLighting", Math.Clamp(Tuning.ComposerLighting, 0f, 2f));
            _compositeShader.SetFloat("uVolumetricDensity", Math.Clamp(Tuning.ComposerVolumetricDensity, 0f, 2f) * comp.VolumetricDensityScale);
            _compositeShader.SetInt("uDissolveAtTail", comp.DissolveAtTail ? 1 : 0);
            _compositeShader.SetFloat("uLoopPhase", loopPhase);
            _compositeShader.SetFloat("uAudioNudge", audioNudge);

            _compositeShader.SetFloat("uGrayscale", Math.Clamp(Tuning.ComposerGrayscale, 0f, 1f));
            _compositeShader.SetInt("uMirrorX", Tuning.ComposerMirrorX ? 1 : 0);
            _compositeShader.SetInt("uMirrorY", Tuning.ComposerMirrorY ? 1 : 0);
            _compositeShader.SetFloat("uGradeLift", Math.Clamp(Tuning.ComposerGradeLift, -0.5f, 0.5f));
            _compositeShader.SetFloat("uGradeGamma", Math.Clamp(Tuning.ComposerGradeGamma, 0.2f, 3.0f));
            _compositeShader.SetFloat("uGradeGain", Math.Clamp(Tuning.ComposerGradeGain, 0f, 2f));
            _compositeShader.SetFloat("uVignette", Math.Clamp(Tuning.ComposerVignette, 0f, 1f));

            bool videoReady = _videoTexture != null;
            _compositeShader.SetInt("uVideoReady", videoReady ? 1 : 0);

            GL.ActiveTexture(TextureUnit.Texture0);
            if (videoReady) _videoTexture!.Bind(TextureUnit.Texture0);
            else GL.BindTexture(TextureTarget.Texture2D, 0);
            _compositeShader.SetInt("uVideoTex", 0);

            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, _childTarget.ColorTextureId);
            _compositeShader.SetInt("uChildTex", 1);

            _quad.Draw();
        }

        public void Unload()
        {
            _childWorld?.Unload();
            _childTarget?.Dispose();
            _videoTexture?.Dispose();
            _decoder?.Dispose();
            _compositeShader?.Dispose();
            _quad?.Dispose();
            Console.WriteLine("[VisualComposer] Unloaded (video decoder process terminated).");
        }
    }
}
