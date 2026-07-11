using CosmicEngine.App.Audio;
using System;
using System.Threading;

namespace CosmicEngine.App.Engine
{
    /// <summary>
    /// Dashboard-Only Launcher Mode (`--dashboard-only`): entry point for starting
    /// just the local dashboard/control server without immediately opening any
    /// visual. Unlike <see cref="CosmicEngineApp.Run"/>, which always constructs a
    /// GameWindow right away (defaulting to StellarNursery), this class starts
    /// AudioEngine + ControlServer and then waits - no world, no OpenGL window,
    /// no render loop - until the user picks a scene from the dashboard.
    ///
    /// Window/GL creation must happen on the main thread (a hard macOS/Cocoa
    /// requirement for NSWindow) - it can't simply be done from ControlServer's
    /// background HTTP thread when a launch request arrives. So this class owns a
    /// simple wait loop on the main thread: ControlServer's HTTP thread only ever
    /// sets a pending-launch/pending-quit flag here (the same pattern
    /// CosmicEngineApp already uses internally for its own dashboard-driven
    /// switch/quit requests, applied one level up, before any CosmicEngineApp
    /// exists yet) - the actual GameWindow construction happens back on this
    /// thread, once a launch is requested.
    /// </summary>
    public static class DashboardHost
    {
        private static volatile string? _pendingLaunchWorld;
        private static volatile string? _pendingLaunchProfile;
        private static volatile bool    _quitRequested;

        public static bool IsRequested(string[] args) =>
            Array.IndexOf(args, "--dashboard-only") >= 0;

        /// <summary>Called from ControlServer's HTTP thread (POST /launch) when no scene is running yet.</summary>
        public static void RequestLaunch(string? worldName, string? profileName)
        {
            _pendingLaunchWorld   = worldName ?? WorldSelector.DefaultWorldName;
            _pendingLaunchProfile = profileName;
        }

        /// <summary>Called from ControlServer's HTTP thread (POST /quit) when no scene is running yet.</summary>
        public static void RequestQuit() => _quitRequested = true;

        public static void Run()
        {
            AudioEngine.Start();
            ControlServer.Start();
            Console.WriteLine("[Dashboard] Dashboard-only mode: no visual running yet.");
            Console.WriteLine("[Dashboard] Choose a scene from http://localhost:8080 to start one.");

            while (!_quitRequested && _pendingLaunchWorld == null)
            {
                Thread.Sleep(100);
            }

            if (_quitRequested)
            {
                Console.WriteLine("[Dashboard] Quit requested before any scene was launched - shutting down.");
                AudioEngine.Stop();
                ControlServer.Stop();
                return;
            }

            string  world   = _pendingLaunchWorld!;
            string? profile = _pendingLaunchProfile;
            _pendingLaunchWorld   = null;
            _pendingLaunchProfile = null;

            Console.WriteLine($"[Dashboard] Launching {world} ({profile ?? "default profile"}) from the dashboard...");

            // Runs on this (main) thread, as required for GameWindow/GL creation.
            // AudioEngine/ControlServer are already running (started above) and are
            // stopped by this instance's own OnUnload when its window eventually
            // closes - exactly like a normal `dotnet run -- --profile ...` session,
            // just deferred until a scene was actually chosen.
            new CosmicEngineApp().RunFromDashboardHost(world, profile);
        }
    }
}
