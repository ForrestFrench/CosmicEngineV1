using CosmicEngine.App.Audio;
using CosmicEngine.App.Rendering;
using CosmicEngine.App.Worlds.World01;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using System;
using System.IO;

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

        // Runtime diagnostics (Runtime Diagnostics Phase 1) - lightweight, no profile system.
        private const float SmokeTestDurationSeconds = 8f;
        private bool   _smokeTestMode;
        private bool   _diagnosticMode;
        private bool   _exitRequested;

        private float _perfTimer;
        private int   _perfFrameCount;

        private float _runElapsed;
        private int   _runFrameCount;

        private string _glRenderer = "";
        private string _glVendor   = "";
        private string _glVersion  = "";
        private string _glslVersion = "";

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

        public void Run(string[] args)
        {
            ParseArgs(args);

            AudioEngine.Start();
            ControlServer.Start();

            _activeWorld = new StellarNursery(_camera);

            _window.Load        += OnLoad;
            _window.RenderFrame += OnRenderFrame;
            _window.Unload      += OnUnload;

            _window.Run();
        }

        private void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--smoke-test")
                {
                    _smokeTestMode = true;
                }
                else if (args[i] == "--diagnostic" && i + 1 < args.Length && args[i + 1] == "baseline")
                {
                    _diagnosticMode = true;
                    _smokeTestMode  = true; // diagnostic mode needs a bounded sample window too
                    i++;
                }
            }

            if (_smokeTestMode)
                Console.WriteLine($"[Smoke Test] Enabled — will run for {SmokeTestDurationSeconds:F0}s then exit.");
            if (_diagnosticMode)
                Console.WriteLine("[Diagnostic] Baseline report will be written on exit.");
        }

        private void OnLoad()
        {
            _glRenderer  = GL.GetString(StringName.Renderer) ?? "unknown";
            _glVendor    = GL.GetString(StringName.Vendor) ?? "unknown";
            _glVersion   = GL.GetString(StringName.Version) ?? "unknown";
            _glslVersion = GL.GetString(StringName.ShadingLanguageVersion) ?? "unknown";

            Console.WriteLine("[OpenGL] Renderer: " + _glRenderer);
            Console.WriteLine("[OpenGL] Vendor:   " + _glVendor);
            Console.WriteLine("[OpenGL] Version:  " + _glVersion);
            Console.WriteLine("[OpenGL] GLSL:     " + _glslVersion);

            _renderTarget = new RenderTarget(RenderWidth, RenderHeight);
            _activeWorld?.Load();

            Console.WriteLine("[CosmicEngine] Started.");
            Console.WriteLine($"  Internal render resolution: {RenderWidth}x{RenderHeight}");
            Console.WriteLine("  Window can be resized freely - shader cost stays fixed.");
            Console.WriteLine("  Control panel: http://localhost:8080");
        }

        private void OnRenderFrame(FrameEventArgs args)
        {
            if (_exitRequested) return;

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

            RunDiagnostics(dt);
        }

        /// <summary>Logs [Perf] once/sec and, in smoke-test/diagnostic mode, ends the run after a fixed window.</summary>
        private void RunDiagnostics(float dt)
        {
            _perfTimer      += dt;
            _perfFrameCount += 1;
            _runElapsed     += dt;
            _runFrameCount  += 1;

            if (_perfTimer >= 1.0f)
            {
                float fps     = _perfFrameCount / _perfTimer;
                float frameMs = (_perfTimer / _perfFrameCount) * 1000f;
                var   client  = _window.ClientSize;
                string world  = _activeWorld?.GetType().Name ?? "none";
                string audio  = AudioEngine.IsCapturing ? "capturing" : "stopped";

                Console.WriteLine(
                    $"[Perf] fps: {fps:F1} | frame: {frameMs:F1}ms | world: {world} | " +
                    $"window: {client.X}x{client.Y} | target: {RenderWidth}x{RenderHeight} | audio: {audio}");

                _perfTimer      = 0f;
                _perfFrameCount = 0;
            }

            if (_smokeTestMode && !_exitRequested && _runElapsed >= SmokeTestDurationSeconds)
            {
                _exitRequested = true;

                float avgFps     = _runFrameCount / _runElapsed;
                float avgFrameMs = (_runElapsed / _runFrameCount) * 1000f;

                Console.WriteLine(
                    $"[Smoke Test] Complete. avg fps: {avgFps:F1} | avg frame: {avgFrameMs:F1}ms | " +
                    $"duration: {_runElapsed:F1}s | frames: {_runFrameCount}");

                if (_diagnosticMode)
                    WriteDiagnosticReport(avgFps, avgFrameMs);

                _window.Close();
            }
        }

        private void WriteDiagnosticReport(float avgFps, float avgFrameMs)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir       = Path.Combine("DiagnosticReports", $"Baseline_{timestamp}");
            Directory.CreateDirectory(dir);
            string reportPath = Path.Combine(dir, "REPORT.md");

            string world = _activeWorld?.GetType().Name ?? "none";

            string report = $"""
                # Baseline Diagnostic Report

                **Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}

                ## OpenGL
                - Renderer: {_glRenderer}
                - Vendor: {_glVendor}
                - Version: {_glVersion}
                - GLSL: {_glslVersion}

                ## Run
                - World loaded: {world}
                - Build status: succeeded (assumed — this report only runs from a built executable)
                - Run status: completed cleanly via --diagnostic baseline smoke run
                - Sample window: {_runElapsed:F1}s ({_runFrameCount} frames)
                - Average FPS: {avgFps:F1}
                - Average frame time: {avgFrameMs:F1}ms

                ## Known limitations
                - No RenderScale / performance-profile system exists yet — this is a fixed-resolution, fixed-shader-cost measurement.
                - Sample window is short (~{SmokeTestDurationSeconds:F0}s); not a sustained-load or thermal-throttling test.
                - Not yet run against target/OptiPlex deployment hardware.
                - Audio input reflects whatever capture device was available at run time, not necessarily a live guitar signal.
                """;

            File.WriteAllText(reportPath, report);
            Console.WriteLine($"[Diagnostic] Report written to {reportPath}");
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
