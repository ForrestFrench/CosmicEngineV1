using CosmicEngine.App.Audio;
using CosmicEngine.App.Rendering;
using CosmicEngine.App.Worlds.World01;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        // Visible-frame diagnostic (Baseline Recovery Pass 2) - runs 3 short phases,
        // captures a screenshot + luminance reading per phase, then exits.
        private enum VisualPhase { SolidColor, Gradient, StellarNursery, DensityDebug, RadianceDebug, Done }
        private const float VisualPhaseDurationSeconds = 2f;
        private bool         _visualTestMode;
        private VisualPhase  _visualPhase = VisualPhase.SolidColor;
        private float        _visualPhaseElapsed;
        private bool         _visualPhaseCaptured;
        private string       _visualReportDir = "";
        private ShaderProgram? _debugGradientShader;
        private FullscreenQuad? _debugQuad;
        private readonly List<(string Label, double AvgLuminance, double MinLuminance, double MaxLuminance, double NonBlackPct, string ScreenshotPath)> _visualResults = new();

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
                else if (args[i] == "--diagnostic" && i + 1 < args.Length && args[i + 1] == "visual")
                {
                    _visualTestMode = true;
                    i++;
                }
            }

            if (_smokeTestMode)
                Console.WriteLine($"[Smoke Test] Enabled — will run for {SmokeTestDurationSeconds:F0}s then exit.");
            if (_diagnosticMode)
                Console.WriteLine("[Diagnostic] Baseline report will be written on exit.");
            if (_visualTestMode)
                Console.WriteLine($"[Visual Test] Enabled — 5 phases x {VisualPhaseDurationSeconds:F0}s (solid color, gradient, StellarNursery, density debug, radiance debug), then exit.");
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

            if (_visualTestMode)
            {
                _debugGradientShader = new ShaderProgram(
                    Path.Combine("Diagnostics", "Shaders", "debug_gradient.vert"),
                    Path.Combine("Diagnostics", "Shaders", "debug_gradient.frag"));
                _debugQuad = new FullscreenQuad();

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _visualReportDir = Path.Combine("DiagnosticReports", $"Visual_{timestamp}");
                Directory.CreateDirectory(_visualReportDir);
                Console.WriteLine($"[Visual Test] Report directory: {_visualReportDir}");
            }

            Console.WriteLine("[CosmicEngine] Started.");
            Console.WriteLine($"  Internal render resolution: {RenderWidth}x{RenderHeight}");
            Console.WriteLine("  Window can be resized freely - shader cost stays fixed.");
            Console.WriteLine("  Control panel: http://localhost:8080");
        }

        private void OnRenderFrame(FrameEventArgs args)
        {
            if (_exitRequested) return;

            float dt = (float)args.Time;

            if (_visualTestMode)
            {
                RunVisualTestFrame(dt);
                return;
            }

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

        /// <summary>
        /// Runs one of three bounded phases (solid color -> gradient shader -> normal
        /// StellarNursery), capturing a screenshot + luminance reading near the end of
        /// each, then exits. See AUDIT.md "Baseline Recovery Pass 2" for why this exists:
        /// it isolates whether a black screen comes from the window/blit path, the
        /// shader/quad pipeline, or the world's own rendering.
        /// </summary>
        private void RunVisualTestFrame(float dt)
        {
            _visualPhaseElapsed += dt;
            _renderTarget!.Bind();

            switch (_visualPhase)
            {
                case VisualPhase.SolidColor:
                    // Bright, unmistakable color with no shader involved at all -
                    // isolates the window/render-target/blit path.
                    GL.ClearColor(1f, 0f, 1f, 1f);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                    break;

                case VisualPhase.Gradient:
                    // Isolated debug shader with no uniforms - isolates the
                    // shader-compile/quad/uniform pipeline from world-specific state.
                    _debugGradientShader!.Use();
                    _debugQuad!.Draw();
                    break;

                case VisualPhase.StellarNursery:
                {
                    // The real world, rendered normally - proves (or disproves) that
                    // the actual scene output is non-black after the uniform fix.
                    // Explicitly reset debug mode so no prior phase's state can leak
                    // into normal-mode output.
                    StellarNursery.DebugMode = 0;
                    var signal = BuildAudioSignal();
                    _camera.Update(dt, signal.Bass1);
                    _activeWorld?.Update(dt, signal);
                    _activeWorld?.Render();
                    break;
                }

                case VisualPhase.DensityDebug:
                {
                    // Same scene, but the shader outputs raw raymarch opacity
                    // (1-transmittance) instead of the final composite - proves
                    // density/cloud structure exists independent of color/exposure.
                    StellarNursery.DebugMode = 1;
                    var signal = BuildAudioSignal();
                    _camera.Update(dt, signal.Bass1);
                    _activeWorld?.Update(dt, signal);
                    _activeWorld?.Render();
                    break;
                }

                case VisualPhase.RadianceDebug:
                {
                    // Same scene, shader outputs raw accumulated emission color
                    // before background/stars/brightness-envelope/gamma - proves
                    // nebula color/structure exists independent of final exposure.
                    StellarNursery.DebugMode = 2;
                    var signal = BuildAudioSignal();
                    _camera.Update(dt, signal.Bass1);
                    _activeWorld?.Update(dt, signal);
                    _activeWorld?.Render();
                    break;
                }
            }

            var fb = _window.FramebufferSize;
            _renderTarget.BlitToScreen(fb.X, fb.Y);

            if (!_visualPhaseCaptured && _visualPhaseElapsed >= VisualPhaseDurationSeconds * 0.75f)
            {
                _visualPhaseCaptured = true;
                CaptureVisualFrame(_visualPhase.ToString());
            }

            _window.SwapBuffers();

            if (_visualPhaseElapsed >= VisualPhaseDurationSeconds)
            {
                _visualPhaseElapsed   = 0f;
                _visualPhaseCaptured  = false;
                _visualPhase = _visualPhase switch
                {
                    VisualPhase.SolidColor     => VisualPhase.Gradient,
                    VisualPhase.Gradient       => VisualPhase.StellarNursery,
                    VisualPhase.StellarNursery => VisualPhase.DensityDebug,
                    VisualPhase.DensityDebug   => VisualPhase.RadianceDebug,
                    VisualPhase.RadianceDebug  => VisualPhase.Done,
                    _                          => VisualPhase.Done
                };

                if (_visualPhase == VisualPhase.Done && !_exitRequested)
                {
                    StellarNursery.DebugMode = 0; // belt-and-suspenders: never leave debug mode active
                    _exitRequested = true;
                    WriteVisualReport();
                    _window.Close();
                }
            }
        }

        /// <summary>Reads the back buffer, logs average luminance / non-black %, and saves a PPM screenshot.</summary>
        private void CaptureVisualFrame(string label)
        {
            var fb = _window.FramebufferSize;
            int w = fb.X, h = fb.Y;
            byte[] pixels = new byte[w * h * 3];

            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Rgb, PixelType.UnsignedByte, pixels);

            double sumLuminance = 0;
            float  minLuminance = 1f;
            float  maxLuminance = 0f;
            long   nonBlackCount = 0;
            byte[]? grayscale = label == "StellarNursery" ? new byte[w * h] : null;

            for (int i = 0, p = 0; i < pixels.Length; i += 3, p++)
            {
                float r = pixels[i]     / 255f;
                float g = pixels[i + 1] / 255f;
                float b = pixels[i + 2] / 255f;
                float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                sumLuminance += luminance;
                if (luminance < minLuminance) minLuminance = luminance;
                if (luminance > maxLuminance) maxLuminance = luminance;
                if (luminance > 0.02f) nonBlackCount++;
                if (grayscale != null) grayscale[p] = (byte)Math.Clamp(luminance * 255f, 0f, 255f);
            }

            long   totalPixels = (long)w * h;
            double avgLuminance = sumLuminance / totalPixels;
            double nonBlackPct  = 100.0 * nonBlackCount / totalPixels;

            Console.WriteLine($"[Visual Test] {label}: average luminance: {avgLuminance:F3}");
            Console.WriteLine($"[Visual Test] {label}: min luminance: {minLuminance:F3} | max luminance: {maxLuminance:F3}");
            Console.WriteLine($"[Visual Test] {label}: non-black pixels: {nonBlackPct:F1}%");

            string screenshotPath = Path.Combine(_visualReportDir, $"{label}.ppm");
            SavePpm(screenshotPath, w, h, pixels);
            Console.WriteLine($"[Visual Test] {label}: screenshot saved to {screenshotPath}");

            if (grayscale != null)
            {
                // Luminance debug view: same frame, mapped to grayscale, to help tell
                // "truly black" apart from "very dim but technically non-zero."
                string lumPath = Path.Combine(_visualReportDir, $"{label}_Luminance.ppm");
                SavePpmGrayscale(lumPath, w, h, grayscale);
                Console.WriteLine($"[Visual Test] {label}: luminance debug view saved to {lumPath}");
            }

            _visualResults.Add((label, avgLuminance, minLuminance, maxLuminance, nonBlackPct, screenshotPath));
        }

        /// <summary>
        /// Writes a raw PPM (P6/NetPBM) screenshot. Chosen over PNG to avoid adding an
        /// image-encoding dependency for this diagnostic pass - view with any NetPBM-aware
        /// tool, or convert (e.g. macOS: `sips -s format png shot.ppm --out shot.png`).
        /// </summary>
        private static void SavePpm(string path, int width, int height, byte[] rgb)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            byte[] header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
            fs.Write(header, 0, header.Length);

            // GL.ReadPixels returns rows bottom-to-top; PPM expects top-to-bottom.
            int stride = width * 3;
            for (int y = height - 1; y >= 0; y--)
                fs.Write(rgb, y * stride, stride);
        }

        /// <summary>Writes a single-channel luminance map as a grayscale PPM (P5).</summary>
        private static void SavePpmGrayscale(string path, int width, int height, byte[] gray)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            byte[] header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
            fs.Write(header, 0, header.Length);

            int stride = width;
            for (int y = height - 1; y >= 0; y--)
                fs.Write(gray, y * stride, stride);
        }

        private void WriteVisualReport()
        {
            string reportPath = Path.Combine(_visualReportDir, "REPORT.md");
            var sb = new StringBuilder();

            sb.AppendLine("# Visible-Frame Diagnostic Report");
            sb.AppendLine();
            sb.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("## OpenGL");
            sb.AppendLine($"- Renderer: {_glRenderer}");
            sb.AppendLine($"- Vendor: {_glVendor}");
            sb.AppendLine($"- Version: {_glVersion}");
            sb.AppendLine($"- GLSL: {_glslVersion}");
            sb.AppendLine();
            sb.AppendLine("## Phases");

            foreach (var r in _visualResults)
            {
                sb.AppendLine($"### {r.Label}");
                sb.AppendLine($"- Average luminance: {r.AvgLuminance:F3}");
                sb.AppendLine($"- Min luminance: {r.MinLuminance:F3} | Max luminance: {r.MaxLuminance:F3}");
                sb.AppendLine($"- Non-black pixels: {r.NonBlackPct:F1}%");
                sb.AppendLine($"- Screenshot: `{Path.GetFileName(r.ScreenshotPath)}` (PPM/NetPBM format)");
                sb.AppendLine();
            }

            sb.AppendLine("## Known limitations");
            sb.AppendLine("- Screenshots are raw PPM, not PNG - no image-encoding dependency was added to keep this pass minimal.");
            sb.AppendLine($"- Each phase samples for {VisualPhaseDurationSeconds:F0}s; this is a pass/fail visibility check, not a sustained visual QA pass.");

            File.WriteAllText(reportPath, sb.ToString());
            Console.WriteLine($"[Visual Test] Report written to {reportPath}");
        }

        private void OnUnload()
        {
            _activeWorld?.Unload();
            _renderTarget?.Dispose();
            _debugGradientShader?.Dispose();
            _debugQuad?.Dispose();
            AudioEngine.Stop();
            ControlServer.Stop();

            // Belt-and-suspenders process exit for bounded test/diagnostic modes: the
            // window is already closing and both background threads above are daemon
            // threads that should let the process exit on their own, but a user-reported
            // issue was Cosmic Engine being left running after test runs. Force it.
            if (_smokeTestMode || _diagnosticMode || _visualTestMode)
                Environment.Exit(0);
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
