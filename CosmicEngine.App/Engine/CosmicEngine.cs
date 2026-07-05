using CosmicEngine.App.Audio;
using CosmicEngine.App.Rendering;
using CosmicEngine.App.Worlds.World01;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using System;

namespace CosmicEngine.App.Engine
{
    /// <summary>
    /// The main engine class. Owns the window, game loop, camera, and active world.
    ///
    /// Rendering pipeline:
    ///   1. World renders into a fixed-size RenderTarget (always 1280x720).
    ///   2. RenderTarget is blitted to the actual window at whatever size it is.
    ///   3. Fragment shader cost is constant regardless of window size.
    ///
    /// To switch worlds: replace _activeWorld with a different IWorld instance.
    /// </summary>
    public class CosmicEngineApp
    {
        // Internal render resolution - always fixed regardless of window size.
        // Increase this on machines with discrete GPUs for crisper output.
        private const int RenderWidth  = 1280;
        private const int RenderHeight = 720;

        private readonly GameWindow _window;
        private readonly Camera     _camera;
        private IWorld?             _activeWorld;
        private RenderTarget?       _renderTarget;

        private float _debugTimer;

        public CosmicEngineApp()
        {
            var nativeSettings = new NativeWindowSettings()
            {
                ClientSize = new OpenTK.Mathematics.Vector2i(RenderWidth, RenderHeight),
                Title      = "Cosmic Engine - Stellar Nursery"
            };

            _window        = new GameWindow(GameWindowSettings.Default, nativeSettings);
            _window.VSync  = VSyncMode.On;
            _camera        = new Camera();
        }

        public void Run()
        {
            AudioEngine.Start();
            ControlServer.Start();

            _activeWorld = new StellarNursery(_camera);

            _window.Load        += OnLoad;
            _window.RenderFrame += OnRenderFrame;
            _window.Unload      += OnUnload;

            _window.Run();
        }

        private void OnLoad()
        {
            _renderTarget = new RenderTarget(RenderWidth, RenderHeight);
            _activeWorld?.Load();

            Console.WriteLine("[CosmicEngine] Started.");
            Console.WriteLine($"  Internal render resolution: {RenderWidth}x{RenderHeight}");
            Console.WriteLine("  Window can be resized freely - shader cost stays fixed.");
            Console.WriteLine("  Control panel: http://localhost:8080");
        }

        private void OnRenderFrame(FrameEventArgs args)
        {
            float dt = (float)args.Time;

            var signal = BuildAudioSignal();

            _camera.Update(dt, signal.Bass1);

            _debugTimer += dt;
            if (_debugTimer >= 1.0f)
            {
                _debugTimer = 0f;
                Console.WriteLine(
                    $"G1 Level:{signal.Level1:F3} Bass:{signal.Bass1:F3}  |  " +
                    $"G2 Level:{signal.Level2:F3} Bass:{signal.Bass2:F3}");
            }

            // Render world at fixed internal resolution
            _renderTarget!.Bind();
            _activeWorld?.Update(dt, signal);
            _activeWorld?.Render();

            // Scale to actual screen (handles Retina, fullscreen, any window size)
            var fb = _window.FramebufferSize;
            _renderTarget.BlitToScreen(fb.X, fb.Y);

            _window.SwapBuffers();
        }

        private void OnUnload()
        {
            _activeWorld?.Unload();
            _renderTarget?.Dispose();
            AudioEngine.Stop();
            ControlServer.Stop();
        }

        private static AudioSignal BuildAudioSignal() => new AudioSignal
        {
            Bass1   = AudioEngine.Guitar1.Bass,
            Mid1    = AudioEngine.Guitar1.Mid,
            Treble1 = AudioEngine.Guitar1.Treble,
            Level1  = AudioEngine.Guitar1.Level,
            Bass2   = AudioEngine.Guitar2.Bass,
            Mid2    = AudioEngine.Guitar2.Mid,
            Treble2 = AudioEngine.Guitar2.Treble,
            Level2  = AudioEngine.Guitar2.Level,
        };
    }
}
