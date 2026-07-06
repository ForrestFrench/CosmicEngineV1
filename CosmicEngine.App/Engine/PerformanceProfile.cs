using System;

namespace CosmicEngine.App.Engine
{
    /// <summary>
    /// P1 (RenderScale + Live/Safe Profiles): a named performance tier. Each profile
    /// is currently just a RenderScale multiplier applied to the base 1280x720 design
    /// resolution to produce the actual RenderTarget size - no shader-quality tiers
    /// exist yet (out of scope for this pass; see ROADMAP.md P1).
    /// </summary>
    public readonly struct PerformanceProfile
    {
        public string Name { get; }
        public float RenderScale { get; }
        public string Purpose { get; }

        private PerformanceProfile(string name, float renderScale, string purpose)
        {
            Name = name;
            RenderScale = renderScale;
            Purpose = purpose;
        }

        /// <summary>Live/stage safety profile - lower render cost. First target for OptiPlex validation (ROADMAP.md P2).</summary>
        public static readonly PerformanceProfile Safe = new("Safe", 0.50f, "live/stage safety - lower render cost");

        /// <summary>Optional middle tier - cheap to include, useful extra data point for the FPS-variance sweep.</summary>
        public static readonly PerformanceProfile Balanced = new("Balanced", 0.75f, "middle ground - not required, included for extra sweep data");

        /// <summary>Full-resolution dev/art profile - current full-resolution comparison baseline.</summary>
        public static readonly PerformanceProfile High = new("High", 1.00f, "full-resolution dev/art comparison baseline");

        public static readonly PerformanceProfile[] All = { Safe, Balanced, High };

        public static readonly string ValidNamesList = "Safe, Balanced, High";

        public static bool TryParse(string name, out PerformanceProfile profile)
        {
            foreach (var p in All)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    profile = p;
                    return true;
                }
            }
            profile = default;
            return false;
        }
    }
}
