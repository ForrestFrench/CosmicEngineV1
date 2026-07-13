using CosmicEngine.App.Audio;
using CosmicEngine.App.Rendering;
using CosmicEngine.App.Worlds.World01;
using CosmicEngine.App.Worlds.World02;
using CosmicEngine.App.Worlds.World03;
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
        // Scene Dashboard v0.1: the currently running "show mode" instance (set in
        // Run(), not RunSweepSubRun() - dashboard-driven live switching only applies
        // to a normal interactive run, never to a bounded PerformanceSweep sub-run).
        // ControlServer reads/writes through this reference from its background HTTP
        // thread; all actual GL/world mutation still happens on the render thread via
        // the pending-request fields below, applied in OnRenderFrame.
        public static CosmicEngineApp? Current { get; private set; }

        // Set from ControlServer's HTTP thread, consumed once per frame on the render
        // thread in OnRenderFrame/ApplyPendingSwitch - GL calls (shader compile, FBO
        // create/destroy) must only ever happen on the render thread.
        private volatile string? _pendingWorldName;
        private volatile string? _pendingProfileName;
        private volatile bool    _quitRequested;
        private volatile bool    _restartRequested;

        // Dashboard status read-back (Scene Dashboard v0.1).
        public string CurrentWorldName   => _worldName;
        public string CurrentProfileName => _profile.Name;
        public int    CurrentRenderWidth  => _renderWidth;
        public int    CurrentRenderHeight => _renderHeight;
        public float  LastObservedFps     { get; private set; }
        public bool   AudioCapturing      => AudioEngine.IsCapturing;

        // Dashboard Show Seed Support: exposes the active world's seed (StellarNursery
        // only - other worlds have no seed concept) for the dashboard's /status endpoint.
        public string CurrentSeedInfo => ActiveWorldSeedInfo();

        /// <summary>Queues a world/profile switch to be applied on the render thread next frame. Either argument may be null to leave that dimension unchanged.</summary>
        public void RequestSwitch(string? worldName, string? profileName)
        {
            if (worldName   != null) _pendingWorldName   = worldName;
            if (profileName != null) _pendingProfileName = profileName;
        }

        /// <summary>Queues a clean shutdown, applied on the render thread next frame.</summary>
        public void RequestQuit() => _quitRequested = true;

        /// <summary>Queues an unload/reload of the current world (same world, same profile) - lets a stuck or visually-odd scene be reset without a full process restart.</summary>
        public void RequestRestart() => _restartRequested = true;

        // Base/design render resolution at PerformanceProfile.RenderScale = 1.0.
        // The window's client size always stays this size (the blit fills whatever
        // window size is actually current); only the internal RenderTarget shrinks
        // or grows with the active profile's RenderScale (P1: RenderScale + Live/Safe
        // Profiles - see ROADMAP.md). Increase this on machines with discrete GPUs
        // for crisper output.
        private const int BaseRenderWidth  = 1280;
        private const int BaseRenderHeight = 720;

        // P1: active performance profile. Defaults to High so normal `dotnet run`
        // (no --profile flag) behaves exactly as it always has (RenderScale 1.00,
        // i.e. no change for the existing dev workflow) - Safe must be explicitly
        // requested via --profile Safe. See ROADMAP.md P1 for the rationale.
        private PerformanceProfile _profile = PerformanceProfile.High;
        private int _renderWidth;
        private int _renderHeight;

        // Lava Lamp Scene Draft v0.1: active world selection. Defaults to
        // StellarNursery (unchanged prior behavior) - LavaLamp must be requested
        // explicitly via --world LavaLamp. See WorldSelector.cs.
        private string _worldName = WorldSelector.DefaultWorldName;

        // Dashboard Show Seed Support: true once an explicit CLI --seed has been
        // parsed. Explicit CLI intent always wins for the initial world load; a
        // scene's registry ShowSeed only fills in when the user didn't ask for a
        // specific seed themselves.
        private bool _explicitSeedProvided;

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

        // P1: set only when this instance is a sub-run driven by PerformanceSweep -
        // suppresses the per-instance AudioEngine/ControlServer lifecycle and the
        // forced process exit, since the sweep orchestrator owns both across all
        // sub-runs and decides when the whole sweep (and process) actually ends.
        private bool  _perfSweepSubRun;
        private float? _sweepDurationOverride;
        private float EffectiveSmokeTestDuration => _sweepDurationOverride ?? SmokeTestDurationSeconds;

        private float _perfTimer;
        private int   _perfFrameCount;

        private float _runElapsed;
        private int   _runFrameCount;
        private float _minObservedFps = float.MaxValue;

        // P1: last completed bounded run's summary, read back by PerformanceSweep
        // after _window.Run() returns (OnUnload has already fired by then).
        public float LastAvgFps      { get; private set; }
        public float LastAvgFrameMs  { get; private set; }

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

        // Stellar Nursery Showability Audit: bounded multi-timepoint capture of the
        // *normal* render path (uDebugMode 0, real Update/Render, no synthetic debug
        // shader) at fixed elapsed times, so motion can be judged the same way a user
        // watching the dashboard would see it - not just a single frame. This is
        // diagnostic instrumentation only; it does not change any world's rendering.
        private bool   _motionTestMode;
        private float  _motionElapsed;
        private readonly bool[] _motionCaptured = new bool[3]; // t1, t5, t15
        private static readonly float[] MotionCaptureTimes = { 1f, 5f, 15f };
        private const float MotionTestDurationSeconds = 16f;
        private string _motionReportDir = "";
        private readonly List<(string Label, byte[] Pixels, int Width, int Height)> _motionFrames = new();

        public CosmicEngineApp()
        {
            var nativeSettings = new NativeWindowSettings()
            {
                ClientSize   = new OpenTK.Mathematics.Vector2i(BaseRenderWidth, BaseRenderHeight),
                Title        = "Cosmic Engine - Stellar Nursery",
                StartFocused = false
            };

            _window        = new GameWindow(GameWindowSettings.Default, nativeSettings);
            _window.VSync  = VSyncMode.On;
            _camera        = new Camera();
        }

        public void Run(string[] args)
        {
            ParseArgs(args);
            _window.Title = $"Cosmic Engine - {_worldName}";
            Current = this;

            AudioEngine.Start();
            ControlServer.Start();

            _activeWorld = WorldSelector.Create(_worldName, _camera);

            _window.Load        += OnLoad;
            _window.RenderFrame += OnRenderFrame;
            _window.Unload      += OnUnload;

            _window.Run();
        }

        /// <summary>
        /// Dashboard-Only Launcher Mode: starts a scene chosen from the dashboard
        /// after the process was launched with `--dashboard-only` (see
        /// <see cref="DashboardHost"/>). Unlike <see cref="Run"/>, this does NOT
        /// call AudioEngine.Start()/ControlServer.Start() - DashboardHost already
        /// started both before any scene was selected - and there is no CLI `args`
        /// to parse, since the world/profile came from the dashboard's
        /// `POST /launch`, not the command line. `_explicitSeedProvided` is left
        /// false (no CLI --seed was given), so OnLoad's existing
        /// ApplyShowSeedIfAvailable() call still applies StellarNursery's known-good
        /// show seed (777) automatically, exactly as it does for any other
        /// dashboard-driven launch/switch.
        /// </summary>
        public void RunFromDashboardHost(string? worldName, string? profileName)
        {
            if (worldName != null && WorldSelector.TryParse(worldName, out var resolvedWorld))
                _worldName = resolvedWorld;
            else
                _worldName = WorldSelector.DefaultWorldName;

            if (profileName != null && PerformanceProfile.TryParse(profileName, out var parsedProfile))
                _profile = parsedProfile;
            // else: leave the default profile (High) - the dashboard's Launch
            // Safe/Launch High buttons always send an explicit profile, so this
            // fallback shouldn't normally trigger; mirrors ParseArgs's existing
            // safe-fallback spirit for a missing/invalid value.

            _window.Title = $"Cosmic Engine - {_worldName}";
            Current = this;

            _activeWorld = WorldSelector.Create(_worldName, _camera);

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
                else if (args[i] == "--diagnostic" && i + 1 < args.Length && args[i + 1] == "motion")
                {
                    _motionTestMode = true;
                    i++;
                }
                else if (args[i] == "--profile" && i + 1 < args.Length)
                {
                    string requested = args[i + 1];
                    if (PerformanceProfile.TryParse(requested, out var parsed))
                    {
                        _profile = parsed;
                    }
                    else
                    {
                        // P1: fall back to Safe (not High) on an invalid profile name -
                        // Safe is the deliberate failsafe choice here, matching its
                        // live/stage-safety purpose rather than silently running at
                        // full cost after a typo.
                        Console.WriteLine(
                            $"[Profile] WARNING: unknown profile '{requested}'. Valid profiles: {PerformanceProfile.ValidNamesList}. Falling back to Safe.");
                        _profile = PerformanceProfile.Safe;
                    }
                    i++;
                }
                else if (args[i] == "--world" && i + 1 < args.Length)
                {
                    string requestedWorld = args[i + 1];
                    if (WorldSelector.TryParse(requestedWorld, out var resolvedWorld))
                    {
                        _worldName = resolvedWorld;
                    }
                    else
                    {
                        // Fall back to the default (StellarNursery) rather than exit -
                        // same non-destructive pattern as an invalid --profile name.
                        Console.WriteLine(
                            $"[World] WARNING: unknown world '{requestedWorld}'. Valid worlds: {WorldSelector.ValidNamesList}. Falling back to {WorldSelector.DefaultWorldName}.");
                        _worldName = WorldSelector.DefaultWorldName;
                    }
                    i++;
                }
                else if (args[i] == "--seed" && i + 1 < args.Length)
                {
                    // Stellar Nursery Showability Consistency Fix: makes the seed used by
                    // a normal run/dashboard show-mode session, and every bounded
                    // diagnostic mode, explicit and reviewable from the command line -
                    // sets the same COSMICENGINE_SEED environment variable
                    // StellarNursery.Load() already reads, so no changes were needed
                    // there. Root cause of the prior rejected package: --diagnostic
                    // motion was run with no seed control, so StellarNursery.Load()
                    // picked a random seed from the known-good pool - not necessarily
                    // the same seed used for the separately-captured --diagnostic visual
                    // reference frame, producing two genuinely different (not just
                    // differently-timed) frames that were incorrectly claimed as the
                    // same. --seed removes that ambiguity for review captures.
                    string requestedSeed = args[i + 1];
                    Environment.SetEnvironmentVariable("COSMICENGINE_SEED", requestedSeed);
                    _explicitSeedProvided = true;
                    Console.WriteLine($"[Seed] --seed {requestedSeed} -> COSMICENGINE_SEED set for this run.");
                    i++;
                }
            }

            if (_smokeTestMode)
                Console.WriteLine($"[Smoke Test] Enabled — will run for {EffectiveSmokeTestDuration:F0}s then exit.");
            if (_diagnosticMode)
                Console.WriteLine("[Diagnostic] Baseline report will be written on exit.");
            if (_visualTestMode)
                Console.WriteLine($"[Visual Test] Enabled — 5 phases x {VisualPhaseDurationSeconds:F0}s (solid color, gradient, StellarNursery, density debug, radiance debug), then exit.");
            if (_motionTestMode)
                Console.WriteLine($"[Motion Test] Enabled — normal render path, captures at t=1s/5s/15s, exits at {MotionTestDurationSeconds:F0}s.");
            Console.WriteLine($"[Profile] Using profile: {_profile.Name} (RenderScale {_profile.RenderScale:F2}) — {_profile.Purpose}");
            Console.WriteLine($"[World] Using world: {_worldName}");
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

            // P1: actual RenderTarget size is scaled by the active profile's
            // RenderScale - this is what makes RenderScale affect real render cost,
            // not just a logged number. Window client size is unaffected (stays at
            // BaseRenderWidth/Height); BlitToScreen still scales the smaller/larger
            // target up/down to fill whatever the window's framebuffer size is.
            _renderWidth  = (int)MathF.Round(BaseRenderWidth  * _profile.RenderScale);
            _renderHeight = (int)MathF.Round(BaseRenderHeight * _profile.RenderScale);
            _renderTarget = new RenderTarget(_renderWidth, _renderHeight);

            // Lava Lamp Scene Draft v0.1: profile-aware blob count (harmless no-op
            // when StellarNursery is the active world - only LavaLampScene reads it).
            LavaLampScene.BlobCount = _profile.Name == "High" ? 8 : 6;

            // Wind Turbine Fire Phase 1 v0.1: profile-aware ember count (harmless
            // no-op unless WindTurbineFire is the active world), same pattern as
            // LavaLampScene.BlobCount above.
            WindTurbineFireScene.EmberCount = _profile.Name == "High" ? 24 : 12;

            // Dashboard Show Seed Support: only fills in when the user didn't already
            // ask for a specific seed via CLI --seed - explicit intent always wins.
            if (!_explicitSeedProvided)
                ApplyShowSeedIfAvailable(_worldName);

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

            if (_motionTestMode)
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _motionReportDir = Path.Combine("DiagnosticReports", $"Motion_{timestamp}");
                Directory.CreateDirectory(_motionReportDir);
                Console.WriteLine($"[Motion Test] Report directory: {_motionReportDir}");
            }

            Console.WriteLine("[CosmicEngine] Started.");
            Console.WriteLine($"  Profile: {_profile.Name} (RenderScale {_profile.RenderScale:F2})");
            Console.WriteLine($"  Internal render resolution: {_renderWidth}x{_renderHeight} (base design {BaseRenderWidth}x{BaseRenderHeight})");
            Console.WriteLine("  Window can be resized freely - shader cost stays fixed per profile.");
            Console.WriteLine("  Control panel: http://localhost:8080");
        }

        private void OnRenderFrame(FrameEventArgs args)
        {
            if (_exitRequested) return;

            // Scene Dashboard v0.1: dashboard-driven quit/switch requests only apply to
            // a normal interactive "show mode" run - never to a bounded smoke-test,
            // diagnostic, or visual-test run, so existing bounded-diagnostic behavior
            // (fixed duration, forced exit) is completely unaffected.
            if (!_smokeTestMode && !_diagnosticMode && !_visualTestMode && !_motionTestMode)
            {
                if (_quitRequested)
                {
                    _exitRequested = true;
                    _window.Close();
                    return;
                }

                if (_restartRequested)
                {
                    _restartRequested = false;
                    _activeWorld?.Unload();
                    _activeWorld = WorldSelector.Create(_worldName, _camera);
                    _activeWorld.Load();
                    Console.WriteLine($"[Dashboard] Restarted world={_worldName} profile={_profile.Name}");
                }

                if (_pendingWorldName != null || _pendingProfileName != null)
                    ApplyPendingSwitch();
            }

            float dt = (float)args.Time;

            if (_visualTestMode)
            {
                RunVisualTestFrame(dt);
                return;
            }

            if (_motionTestMode)
            {
                RunMotionTestFrame(dt);
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

        /// <summary>
        /// Scene Dashboard v0.1: applies a queued world and/or profile switch requested
        /// via the dashboard. Runs on the render thread (called from OnRenderFrame), so
        /// it is safe to create/destroy GL resources here (shader compile/link, FBO
        /// create/delete) - the same rule every other GL call in this class already
        /// follows. Old world/render-target resources are disposed before new ones are
        /// created, mirroring the same Load()/Unload() and RenderTarget lifecycle
        /// already used for a normal single-run startup.
        /// </summary>
        /// <summary>
        /// Dashboard Show Seed Support: sets COSMICENGINE_SEED to the given world's
        /// registered SceneDefinition.ShowSeed, if it has one - reuses the exact same
        /// env-var override StellarNursery.Load() and CLI --seed already read, so no
        /// changes were needed there. No-op for worlds with no ShowSeed (e.g. LavaLamp).
        /// </summary>
        private static void ApplyShowSeedIfAvailable(string worldName)
        {
            if (SceneRegistry.TryParse(worldName, out var scene) && scene.ShowSeed != null)
            {
                Environment.SetEnvironmentVariable("COSMICENGINE_SEED", scene.ShowSeed);
                Console.WriteLine($"[Seed] Using show seed {scene.ShowSeed} for {scene.DisplayName}.");
            }
        }

        private void ApplyPendingSwitch()
        {
            string? newWorld       = _pendingWorldName;
            string? newProfileName = _pendingProfileName;
            _pendingWorldName   = null;
            _pendingProfileName = null;

            bool worldChanged = newWorld != null &&
                !string.Equals(newWorld, _worldName, StringComparison.OrdinalIgnoreCase);

            bool profileChanged = false;
            PerformanceProfile parsedProfile = _profile;
            if (newProfileName != null && PerformanceProfile.TryParse(newProfileName, out var parsed))
            {
                parsedProfile   = parsed;
                profileChanged  = !string.Equals(parsed.Name, _profile.Name, StringComparison.OrdinalIgnoreCase);
            }

            if (!worldChanged && !profileChanged)
                return;

            if (worldChanged)
            {
                _activeWorld?.Unload();
                _worldName   = newWorld!;
                _activeWorld = WorldSelector.Create(_worldName, _camera);

                // Dashboard Show Seed Support: every dashboard-triggered switch into a
                // scene with a known-good ShowSeed uses it, so showing bandmates never
                // depends on random-seed luck - this is unconditional (not gated behind
                // _explicitSeedProvided) because a dashboard switch is, by definition, a
                // deliberate user action distinct from the process's initial CLI launch.
                ApplyShowSeedIfAvailable(_worldName);
            }

            if (profileChanged)
            {
                _profile      = parsedProfile;
                _renderWidth  = (int)MathF.Round(BaseRenderWidth  * _profile.RenderScale);
                _renderHeight = (int)MathF.Round(BaseRenderHeight * _profile.RenderScale);
                _renderTarget?.Dispose();
                _renderTarget = new RenderTarget(_renderWidth, _renderHeight);
                LavaLampScene.BlobCount = _profile.Name == "High" ? 8 : 6;
                WindTurbineFireScene.EmberCount = _profile.Name == "High" ? 24 : 12;
            }

            if (worldChanged)
                _activeWorld?.Load();

            _window.Title = $"Cosmic Engine - {_worldName}";
            Console.WriteLine(
                $"[Dashboard] Switched to world={_worldName} profile={_profile.Name} " +
                $"({_renderWidth}x{_renderHeight})");
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

                if (fps < _minObservedFps) _minObservedFps = fps;
                LastObservedFps = fps;

                Console.WriteLine(
                    $"[Perf] profile: {_profile.Name} | scale: {_profile.RenderScale:F2} | fps: {fps:F1} | frame: {frameMs:F1}ms | world: {world} | " +
                    $"window: {client.X}x{client.Y} | target: {_renderWidth}x{_renderHeight} | audio: {audio}");

                _perfTimer      = 0f;
                _perfFrameCount = 0;
            }

            if (_smokeTestMode && !_exitRequested && _runElapsed >= EffectiveSmokeTestDuration)
            {
                _exitRequested = true;

                float avgFps     = _runFrameCount / _runElapsed;
                float avgFrameMs = (_runElapsed / _runFrameCount) * 1000f;
                LastAvgFps     = avgFps;
                LastAvgFrameMs = avgFrameMs;

                Console.WriteLine(
                    $"[Smoke Test] Complete. profile: {_profile.Name} | avg fps: {avgFps:F1} | avg frame: {avgFrameMs:F1}ms | " +
                    $"min observed fps: {_minObservedFps:F1} | duration: {_runElapsed:F1}s | frames: {_runFrameCount}");

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

                ## Profile (P1: RenderScale + Live/Safe Profiles)
                - Profile: {_profile.Name} ({_profile.Purpose})
                - RenderScale: {_profile.RenderScale:F2}
                - Render target size: {_renderWidth}x{_renderHeight} (base design {BaseRenderWidth}x{BaseRenderHeight})

                ## Run
                - World loaded: {world}
                - Build status: succeeded (assumed — this report only runs from a built executable)
                - Run status: completed cleanly via --diagnostic baseline smoke run
                - Sample window: {_runElapsed:F1}s ({_runFrameCount} frames)
                - Average FPS: {avgFps:F1}
                - Minimum observed FPS (per-second sample): {_minObservedFps:F1}
                - Average frame time: {avgFrameMs:F1}ms

                ## Known limitations
                - Single-run measurement — do not treat this alone as a performance claim; see `--diagnostic perf-sweep` for repeated-run evidence across profiles.
                - Sample window is short (~{EffectiveSmokeTestDuration:F0}s); not a sustained-load or thermal-throttling test.
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

        /// <summary>
        /// Stellar Nursery Showability Audit: runs the world's completely normal
        /// Update()/Render() path (uDebugMode 0, real audio signal, real camera/profile
        /// - nothing synthetic) and captures the actual back buffer at t=1s/5s/15s of
        /// elapsed run time, then exits. Unlike --diagnostic visual (which captures one
        /// frame per synthetic phase), this exists to answer one question: does the
        /// currently-active world visibly change over the timescale a person watching
        /// it would notice?
        /// </summary>
        private void RunMotionTestFrame(float dt)
        {
            var signal = BuildAudioSignal();
            _camera.Update(dt, signal.Bass1);
            _motionElapsed += dt;

            _renderTarget!.Bind();
            _activeWorld?.Update(dt, signal);
            _activeWorld?.Render();

            var fb = _window.FramebufferSize;
            _renderTarget.BlitToScreen(fb.X, fb.Y);
            _window.SwapBuffers();

            for (int i = 0; i < MotionCaptureTimes.Length; i++)
            {
                if (!_motionCaptured[i] && _motionElapsed >= MotionCaptureTimes[i])
                {
                    _motionCaptured[i] = true;
                    CaptureMotionFrame($"T{MotionCaptureTimes[i]:F0}");
                }
            }

            if (_motionElapsed >= MotionTestDurationSeconds && !_exitRequested)
            {
                _exitRequested = true;
                WriteMotionReport();
                _window.Close();
            }
        }

        private void CaptureMotionFrame(string label)
        {
            var fb = _window.FramebufferSize;
            int w = fb.X, h = fb.Y;
            byte[] pixels = new byte[w * h * 3];
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Rgb, PixelType.UnsignedByte, pixels);

            string path = Path.Combine(_motionReportDir, $"{label}.ppm");
            SavePpm(path, w, h, pixels);
            Console.WriteLine($"[Motion Test] {label}: screenshot saved to {path} ({w}x{h})");

            _motionFrames.Add((label, pixels, w, h));
        }

        /// <summary>
        /// Stellar Nursery Showability Consistency Fix: reports the active world's actual
        /// seed (StellarNursery only - other worlds don't have a seed concept) so every
        /// diagnostic report is self-documenting about which seed produced it. Root cause
        /// of the prior rejected package: no diagnostic report logged this, so two
        /// separately-captured frames using different seeds were incorrectly compared as
        /// if they were the same frame.
        /// </summary>
        private string ActiveWorldSeedInfo() =>
            _activeWorld is StellarNursery sn ? $"{sn.Seed:F2}" : "n/a (world has no seed concept)";

        /// <summary>Reports mean/max per-channel pixel difference and % of pixels that changed meaningfully between consecutive captures - a proxy for human-visible motion, not a substitute for looking at the frames.</summary>
        private void WriteMotionReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Motion Test Report");
            sb.AppendLine();
            sb.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"**World:** {_worldName} | **Profile:** {_profile.Name} | **Seed:** {ActiveWorldSeedInfo()}");
            sb.AppendLine();

            for (int i = 1; i < _motionFrames.Count; i++)
            {
                var prev = _motionFrames[i - 1];
                var curr = _motionFrames[i];
                var (meanAbsDiff, maxDiff, pctChanged) = ComputeFrameDiff(prev.Pixels, curr.Pixels);
                sb.AppendLine($"## {prev.Label} -> {curr.Label}");
                sb.AppendLine($"- Mean absolute per-channel difference (0-255 scale): {meanAbsDiff:F3}");
                sb.AppendLine($"- Max per-channel difference (0-255 scale): {maxDiff}");
                sb.AppendLine($"- Percent of pixels changed by more than 2/255 in any channel: {pctChanged:F2}%");
                sb.AppendLine();

                Console.WriteLine(
                    $"[Motion Test] {prev.Label}->{curr.Label}: mean diff {meanAbsDiff:F3}/255, " +
                    $"max diff {maxDiff}/255, {pctChanged:F2}% pixels changed");
            }

            string reportPath = Path.Combine(_motionReportDir, "REPORT.md");
            File.WriteAllText(reportPath, sb.ToString());
            Console.WriteLine($"[Motion Test] Report written to {reportPath}");
        }

        private static (double meanAbsDiff, int maxDiff, double pctChanged) ComputeFrameDiff(byte[] a, byte[] b)
        {
            long sum = 0;
            int  max = 0;
            long changedPixels = 0;
            int  pixelCount = a.Length / 3;

            for (int i = 0; i < a.Length; i += 3)
            {
                int dr = Math.Abs(a[i]     - b[i]);
                int dg = Math.Abs(a[i + 1] - b[i + 1]);
                int db = Math.Abs(a[i + 2] - b[i + 2]);
                int d  = Math.Max(dr, Math.Max(dg, db));

                sum += dr + dg + db;
                if (d > max) max = d;
                if (d > 2) changedPixels++;
            }

            double meanAbsDiff = sum / (double)a.Length;
            double pctChanged  = 100.0 * changedPixels / pixelCount;
            return (meanAbsDiff, max, pctChanged);
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
            sb.AppendLine($"**World:** {_worldName} | **Profile:** {_profile.Name} | **Seed:** {ActiveWorldSeedInfo()}");
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
            if (Current == this) Current = null;

            _activeWorld?.Unload();
            _renderTarget?.Dispose();
            _debugGradientShader?.Dispose();
            _debugQuad?.Dispose();

            // P1: a perf-sweep sub-run doesn't own AudioEngine/ControlServer or the
            // process lifetime - PerformanceSweep starts/stops those once for the
            // whole sweep and decides when the process actually exits, so this
            // instance must not stop them or force-exit out from under it.
            if (!_perfSweepSubRun)
            {
                AudioEngine.Stop();
                ControlServer.Stop();
            }

            // Belt-and-suspenders process exit for bounded test/diagnostic modes: the
            // window is already closing and both background threads above are daemon
            // threads that should let the process exit on their own, but a user-reported
            // issue was Cosmic Engine being left running after test runs. Force it.
            if ((_smokeTestMode || _diagnosticMode || _visualTestMode || _motionTestMode) && !_perfSweepSubRun)
                Environment.Exit(0);
        }

        /// <summary>
        /// P1: runs one bounded sub-run of a specific profile as part of a
        /// PerformanceSweep, reusing the exact same smoke-test measurement path as
        /// `--smoke-test` (same [Perf] sampling, same avg-fps/frame-time computation)
        /// so sweep numbers are directly comparable to a normal single-run smoke test.
        /// Does not start/stop AudioEngine/ControlServer or force-exit the process -
        /// the caller (PerformanceSweep) owns both across the whole sweep.
        /// </summary>
        public PerfSweepRunResult RunSweepSubRun(PerformanceProfile profile, float durationSeconds, string worldName = WorldSelector.DefaultWorldName)
        {
            _profile               = profile;
            _worldName             = worldName;
            _smokeTestMode         = true;
            _perfSweepSubRun       = true;
            _sweepDurationOverride = durationSeconds;

            _activeWorld = WorldSelector.Create(_worldName, _camera);

            _window.Load        += OnLoad;
            _window.RenderFrame += OnRenderFrame;
            _window.Unload      += OnUnload;

            string exitStatus = "ok";
            try
            {
                _window.Run();
            }
            catch (Exception ex)
            {
                exitStatus = $"error: {ex.Message}";
            }
            finally
            {
                // Each sub-run's CosmicEngineApp constructs its own GameWindow, and
                // GameWindow.Close() only stops its render loop - it does not tear
                // down the native OS window. Without an explicit Dispose() here, a
                // multi-run sweep (one process, many sub-runs) accumulates visible
                // undisposed windows on screen until the whole sweep's single
                // Environment.Exit(0) call at the very end. Dispose immediately so
                // each sub-run's window actually closes before the next one opens.
                _window.Dispose();
            }

            return new PerfSweepRunResult(
                _worldName, profile.Name, profile.RenderScale, _renderWidth, _renderHeight,
                LastAvgFps, LastAvgFrameMs, _minObservedFps,
                _glRenderer, _glVendor, _glVersion,
                AudioEngine.IsCapturing ? "capturing" : "stopped",
                exitStatus);
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
