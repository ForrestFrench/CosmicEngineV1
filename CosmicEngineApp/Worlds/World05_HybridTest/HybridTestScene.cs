using CosmicEngine.App.Audio;
using CosmicEngine.App.Engine;
using CosmicEngine.App.Rendering;
using CosmicEngine.App.Rendering.Video;
using CosmicEngine.App.Worlds.World01;
using OpenTK.Graphics.OpenGL4;
using System;
using System.IO;

namespace CosmicEngine.App.Worlds.World05
{
    /// <summary>
    /// World 05: Hybrid Test (prototype) - Phase 1 "Hybrid Proof" (MILESTONE_BREAKDOWN.md).
    /// Composites one hardcoded local video clip (played as a looping GL texture via
    /// IVideoDecoder/FfmpegPipeDecoder/VideoTexture) with one existing, unmodified Cosmic Engine
    /// world (default: StellarNursery) rendered into a private RenderTarget, via a single
    /// mix(video, child, uBlend) composite shader. Deliberately minimal per the architect's Phase 1
    /// scope: one blend uniform, no blend modes, no effect stack, no clip library, no director.
    /// </summary>
    public class HybridTestScene : IWorld
    {
        // Hardcoded clip path (Phase 1 explicitly forbids a clip library/database) - see
        // PHASE1_SUMMARY.md for why this specific VisionBoard/ file was chosen.
        private const string ClipPath =
            "/Volumes/External Hard Drive FCF/CosmicEngine/VisionBoard/140733-775596128.mp4";

        private readonly Camera _camera;
        private readonly Camera _childCamera;

        private IVideoDecoder? _decoder;
        private VideoTexture? _videoTexture;
        private ShaderProgram? _compositeShader;
        private FullscreenQuad? _quad;

        private IWorld? _childWorld;
        private RenderTarget? _childTarget;

        private float _time;
        private bool _decoderFailed;
        private string? _decoderError;

        private static string ShaderPath(string file) =>
            Path.Combine("Worlds", "World05_HybridTest", "Shaders", file);

        public HybridTestScene(Camera camera)
        {
            _camera = camera;
            _childCamera = new Camera();
        }

        public void Load()
        {
            _compositeShader = new ShaderProgram(ShaderPath("hybrid.vert"), ShaderPath("hybrid.frag"));
            _quad = new FullscreenQuad();

            // Child world: default StellarNursery, constructed via the same factory pattern
            // SceneRegistry uses (camera => new StellarNursery(camera)) - Phase 1 keeps this
            // hardcoded rather than wiring a second --world-style selector, per scope.
            _childWorld = new StellarNursery(_childCamera);
            _childWorld.Load();

            // Private RenderTarget for the child world - does not touch the engine's own
            // 1280x720 main RenderTarget. RenderTarget.Bind()/BlitToScreen() manage their own FBO
            // bind state (no hidden global-state assumptions found), so nesting one inside
            // HybridTestScene.Render() and rebinding the outer target afterward is safe - see
            // ARCHITECTURE_CHANGES.md for the audit notes.
            _childTarget = new RenderTarget(1280, 720);

            try
            {
                _decoder = new FfmpegPipeDecoder();
                _decoder.Open(ClipPath);
                _videoTexture = new VideoTexture(_decoder);
            }
            catch (Exception ex)
            {
                // Loud (logged) but non-fatal: the scene still loads and composites the child world
                // alone (video layer left as a solid color) rather than crashing the whole engine -
                // matches VIDEO_SYSTEM_ARCHITECTURE.md §2.2's "all failure modes non-fatal" rule.
                _decoderFailed = true;
                _decoderError = ex.Message;
                Console.WriteLine($"[HybridTest] Video decoder failed to open (scene continues without video layer): {ex.Message}");
            }

            Console.WriteLine($"[HybridTest] Loaded. Clip='{ClipPath}', decoderOk={!_decoderFailed}.");
        }

        public void Update(float deltaTime, AudioSignal audio)
        {
            _time += deltaTime;

            // Child's Update(dt, audioSignal) called every frame so its audio reactivity is fully
            // live inside the composite, per Phase 1's acceptance criteria.
            _childCamera.Update(deltaTime, audio.Bass1);
            _childWorld?.Update(deltaTime, audio);

            // Phase 3 Effect Stack v1: playback speed lives in the decoder (decode-rate pacing),
            // not the shader - set live every frame from the dashboard-tunable value so it applies
            // without a scene/decoder restart.
            if (_decoder != null)
                _decoder.PlaybackSpeed = Tuning.HybridPlaybackSpeed;

            _videoTexture?.Update(); // at most one glTexSubImage2D upload per frame
        }

        public void Render()
        {
            if (_compositeShader == null || _quad == null || _childWorld == null || _childTarget == null)
                return;

            // RenderTarget nesting (audited - see ARCHITECTURE_CHANGES.md): RenderTarget.Bind()/
            // BlitToScreen() carry no hidden global state, but they also don't save/restore
            // whatever FBO was already bound - CosmicEngineApp.OnRenderFrame binds its own main
            // 1280x720 RenderTarget before calling this Render(), so we must capture that binding,
            // render the child into our own private target, then restore the outer binding before
            // the composite draw. This is a minimal, additive nesting pattern - no change to
            // RenderTarget's own bind/unbind behavior was required.
            GL.GetInteger(GetPName.FramebufferBinding, out int outerFbo);

            // 1. Render the child world into its own private target.
            _childTarget.Bind();
            _childWorld.Render();

            // 2. Restore the outer (engine main) RenderTarget's binding + viewport so the composite
            // draw below lands in the same place the engine expects this world's Render() to draw.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, outerFbo);
            GL.Viewport(0, 0, 1280, 720);

            _compositeShader.Use();
            _compositeShader.SetFloat("uTime", _time);
            _compositeShader.SetFloat("uBlend", Math.Clamp(Tuning.HybridBlend, 0f, 1f));

            // Phase 3 Effect Stack v1: uniform-driven effects, applied after the existing blend
            // inside the composite shader (see hybrid.frag). Phase 1 behavior above is unchanged.
            _compositeShader.SetFloat("uGrayscale", Math.Clamp(Tuning.HybridGrayscale, 0f, 1f));
            _compositeShader.SetInt("uMirrorX", Tuning.HybridMirrorX ? 1 : 0);
            _compositeShader.SetInt("uMirrorY", Tuning.HybridMirrorY ? 1 : 0);
            _compositeShader.SetFloat("uGradeLift", Math.Clamp(Tuning.HybridGradeLift, -0.5f, 0.5f));
            _compositeShader.SetFloat("uGradeGamma", Math.Clamp(Tuning.HybridGradeGamma, 0.2f, 3.0f));
            _compositeShader.SetFloat("uGradeGain", Math.Clamp(Tuning.HybridGradeGain, 0f, 2f));
            _compositeShader.SetFloat("uVignette", Math.Clamp(Tuning.HybridVignette, 0f, 1f));

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
            _decoder?.Dispose(); // guarantees ffmpeg process termination on world switch / engine quit
            _compositeShader?.Dispose();
            _quad?.Dispose();
            Console.WriteLine("[HybridTest] Unloaded (video decoder process terminated).");
        }
    }
}
