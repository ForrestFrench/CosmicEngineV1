using CosmicEngine.App.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CosmicEngine.App.Engine
{
    /// <summary>One bounded sub-run's result, collected by PerformanceSweep.</summary>
    public readonly struct PerfSweepRunResult
    {
        public readonly string WorldName;
        public readonly string ProfileName;
        public readonly float  RenderScale;
        public readonly int    RenderWidth;
        public readonly int    RenderHeight;
        public readonly float  AvgFps;
        public readonly float  AvgFrameMs;
        public readonly float  MinObservedFps;
        public readonly string GlRenderer;
        public readonly string GlVendor;
        public readonly string GlVersion;
        public readonly string AudioStatus;
        public readonly string ExitStatus;

        public PerfSweepRunResult(string worldName, string profileName, float renderScale, int renderWidth, int renderHeight,
            float avgFps, float avgFrameMs, float minObservedFps,
            string glRenderer, string glVendor, string glVersion, string audioStatus, string exitStatus)
        {
            WorldName      = worldName;
            ProfileName    = profileName;
            RenderScale    = renderScale;
            RenderWidth    = renderWidth;
            RenderHeight   = renderHeight;
            AvgFps         = avgFps;
            AvgFrameMs     = avgFrameMs;
            MinObservedFps = minObservedFps;
            GlRenderer     = glRenderer;
            GlVendor       = glVendor;
            GlVersion      = glVersion;
            AudioStatus    = audioStatus;
            ExitStatus     = exitStatus;
        }
    }

    /// <summary>
    /// P1 (RenderScale + Live/Safe Profiles + FPS Variance Evidence): runs repeated
    /// bounded sub-runs per profile in-process, so the FPS-variance question (see
    /// ROADMAP.md / AUDIT.md Entry 12) can be investigated with real repeated-run
    /// data instead of single readings. Each sub-run is a fresh CosmicEngineApp/
    /// GameWindow instance driven by RunSweepSubRun(); AudioEngine and ControlServer
    /// are started once for the whole sweep (not per sub-run) since they are static/
    /// singleton-style subsystems not designed to be restarted dozens of times a
    /// minute, and per-run audio status only needs to reflect "still capturing",
    /// which a single continuous session already does correctly.
    /// </summary>
    public static class PerformanceSweep
    {
        private const int   DefaultRunsPerProfile = 10;
        private const float DefaultRunDurationSeconds = 6f;
        private static readonly PerformanceProfile[] DefaultProfiles = { PerformanceProfile.Safe, PerformanceProfile.High };

        public static bool IsRequested(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--diagnostic" && i + 1 < args.Length && args[i + 1] == "perf-sweep")
                    return true;
            }
            return false;
        }

        public static void Run(string[] args)
        {
            var profiles = DefaultProfiles;
            int runsPerProfile = DefaultRunsPerProfile;
            float durationSeconds = DefaultRunDurationSeconds;

            // Lava Lamp Scene Draft v0.1: optional --world <name> so the sweep can
            // target a specific world, same fallback pattern as CosmicEngineApp's
            // own --world/--profile parsing (unknown name -> warning + default).
            string worldName = WorldSelector.DefaultWorldName;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--world" && i + 1 < args.Length)
                {
                    if (WorldSelector.TryParse(args[i + 1], out var resolved))
                    {
                        worldName = resolved;
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[PerfSweep] WARNING: unknown world '{args[i + 1]}'. Valid worlds: {WorldSelector.ValidNamesList}. Falling back to {WorldSelector.DefaultWorldName}.");
                    }
                    i++;
                }
            }

            Console.WriteLine(
                $"[PerfSweep] Starting: {runsPerProfile} runs x {profiles.Length} profiles " +
                $"({string.Join(", ", Array.ConvertAll(profiles, p => p.Name))}) on world '{worldName}', {durationSeconds:F0}s each. " +
                $"Total bounded duration ~{runsPerProfile * profiles.Length * durationSeconds:F0}s.");

            // Started once for the whole sweep - see class doc comment for why.
            AudioEngine.Start();
            ControlServer.Start();

            var results = new List<PerfSweepRunResult>();

            foreach (var profile in profiles)
            {
                for (int run = 1; run <= runsPerProfile; run++)
                {
                    Console.WriteLine($"[PerfSweep] {profile.Name} run {run}/{runsPerProfile}...");
                    var app = new CosmicEngineApp();
                    var result = app.RunSweepSubRun(profile, durationSeconds, worldName);
                    results.Add(result);
                    Console.WriteLine(
                        $"[PerfSweep] {profile.Name} run {run}: avg fps {result.AvgFps:F1} | " +
                        $"min observed fps {result.MinObservedFps:F1} | target {result.RenderWidth}x{result.RenderHeight} | " +
                        $"exit: {result.ExitStatus}");
                }
            }

            AudioEngine.Stop();
            ControlServer.Stop();

            string reportDir = WriteRawResults(results);
            Console.WriteLine($"[PerfSweep] Complete. Raw results written to {reportDir}");

            // Belt-and-suspenders process exit, same rationale as CosmicEngineApp's
            // bounded modes: never leave the process alive after a bounded diagnostic.
            Environment.Exit(0);
        }

        /// <summary>
        /// Writes raw per-run results as CSV plus a minimal plain-text summary.
        /// Deliberately does not compute/format a polished report here - the full
        /// PerformanceProfiles review package (REPORT.md with aggregated statistics,
        /// screenshots, git/audit context) is assembled as a separate step from this
        /// raw data, consistent with every other diagnostic pass in this project.
        /// </summary>
        private static string WriteRawResults(List<PerfSweepRunResult> results)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir = Path.Combine("DiagnosticReports", $"PerfSweep_{timestamp}");
            Directory.CreateDirectory(dir);

            string csvPath = Path.Combine(dir, "raw_results.csv");
            var csv = new StringBuilder();
            csv.AppendLine("world,profile,render_scale,render_width,render_height,avg_fps,avg_frame_ms,min_observed_fps,gl_renderer,gl_vendor,gl_version,audio_status,exit_status");
            foreach (var r in results)
            {
                csv.AppendLine(string.Join(",",
                    r.WorldName, r.ProfileName, r.RenderScale.ToString("F2"), r.RenderWidth, r.RenderHeight,
                    r.AvgFps.ToString("F2"), r.AvgFrameMs.ToString("F2"), r.MinObservedFps.ToString("F2"),
                    $"\"{r.GlRenderer}\"", $"\"{r.GlVendor}\"", $"\"{r.GlVersion}\"",
                    r.AudioStatus, r.ExitStatus));
            }
            File.WriteAllText(csvPath, csv.ToString());

            string summaryPath = Path.Combine(dir, "summary.txt");
            var summary = new StringBuilder();
            summary.AppendLine($"Perf sweep generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            summary.AppendLine($"Total runs: {results.Count}");

            var byProfile = new Dictionary<string, List<PerfSweepRunResult>>();
            foreach (var r in results)
            {
                if (!byProfile.TryGetValue(r.ProfileName, out var list))
                    byProfile[r.ProfileName] = list = new List<PerfSweepRunResult>();
                list.Add(r);
            }

            foreach (var kvp in byProfile)
            {
                var runs = kvp.Value;
                float avg = 0f, min = float.MaxValue, max = float.MinValue;
                foreach (var r in runs)
                {
                    avg += r.AvgFps;
                    if (r.AvgFps < min) min = r.AvgFps;
                    if (r.AvgFps > max) max = r.AvgFps;
                }
                avg /= runs.Count;

                summary.AppendLine();
                summary.AppendLine($"Profile: {kvp.Key}");
                summary.AppendLine($"  Runs: {runs.Count}");
                summary.AppendLine($"  Avg-of-avg FPS: {avg:F1}");
                summary.AppendLine($"  Min avg FPS: {min:F1}");
                summary.AppendLine($"  Max avg FPS: {max:F1}");
                summary.AppendLine($"  Range: {(max - min):F1}");
            }

            File.WriteAllText(summaryPath, summary.ToString());

            return dir;
        }
    }
}
