using CosmicEngine.App.Worlds.World01;
using CosmicEngine.App.Worlds.World02;
using CosmicEngine.App.Worlds.World03;
using CosmicEngine.App.Worlds.World04;
using CosmicEngine.App.Worlds.World05;
using System;

namespace CosmicEngine.App.Engine
{
    /// <summary>
    /// Scene Dashboard v0.1: metadata for one scene, shown on the dashboard and used
    /// to construct the actual IWorld. This is the single source of truth for scene
    /// metadata - both CLI --world parsing (via WorldSelector) and the dashboard's
    /// scene cards read from SceneRegistry rather than keeping separate lists.
    /// </summary>
    public class SceneDefinition
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required string Description { get; init; }
        public required string Status { get; init; }
        public required string DefaultProfile { get; init; }
        public required bool Showable { get; init; }
        public string? ThumbnailPath { get; init; }

        // Dashboard Show Seed Support: a known-good, reproducible seed to use whenever
        // this scene is launched from the dashboard (or as the initial default-startup
        // world) instead of leaving it to random pool selection - see AUDIT.md
        // "Dashboard Show Seed Support" for why (the seed pool has real quality
        // variance; showing bandmates shouldn't depend on random-seed luck). Null for
        // scenes with no seed concept (e.g. LavaLamp). Does not apply to explicit CLI
        // `--seed <value>`, which always takes priority for the initial launch.
        public string? ShowSeed { get; init; }

        public required Func<Camera, IWorld> Factory { get; init; }
    }

    public static class SceneRegistry
    {
        public static readonly SceneDefinition StellarNursery = new()
        {
            Id             = "StellarNursery",
            DisplayName    = "Stellar Nursery",
            Description    = "Volumetric raymarched nebula - the original CosmicEngine scene.",
            Status         = "Accepted baseline / visual polish parked",
            DefaultProfile = "Safe",
            Showable       = true,
            ThumbnailPath  = null,
            ShowSeed       = "777",
            Factory        = camera => new StellarNursery(camera)
        };

        public static readonly SceneDefinition LavaLamp = new()
        {
            Id             = "LavaLamp",
            DisplayName    = "Lava Lamp",
            Description    = "Cheap analytic 2D metaball field - psychedelic, analog-light-show-inspired scene.",
            Status         = "Accepted prototype v0.1",
            DefaultProfile = "Safe",
            Showable       = true,
            ThumbnailPath  = null,
            Factory        = camera => new LavaLampScene(camera)
        };

        public static readonly SceneDefinition WindTurbineFire = new()
        {
            Id             = "WindTurbineFire",
            DisplayName    = "Wind Turbine Fire",
            Description    = "Shader-only industrial-nightmare tableau - wind-turbine silhouettes against smoke and a slow-building ember horizon.",
            Status         = "Prototype v0.1",
            DefaultProfile = "Safe",
            Showable       = true,
            ThumbnailPath  = null,
            ShowSeed       = null,
            Factory        = camera => new WindTurbineFireScene(camera)
        };

        public static readonly SceneDefinition Underwater = new()
        {
            // Id is deliberately unchanged (Phase 1 "Living Water", display-
            // rename only) - renaming it would break --world Underwater CLI
            // usage, existing docs, and every test invocation across this
            // project's history. DisplayName/Description below are updated
            // to reflect the drifting jellyfish/plankton-bloom evolution
            // added this pass, not just the static atmosphere the old copy
            // described.
            Id             = "Underwater",
            DisplayName    = "Abyssal Bloom",
            Description    = "Drifting jellyfish and a bioluminescent plankton bloom, evolving through god rays and caustic shimmer in a living water column.",
            Status         = "Prototype v0.1",
            DefaultProfile = "Safe",
            Showable       = true,
            ThumbnailPath  = null,
            ShowSeed       = null,
            Factory        = camera => new UnderwaterScene(camera)
        };

        // Phase 1 "Hybrid Proof" (video-atoms MILESTONE_BREAKDOWN.md): prototype-only, hardcoded
        // single-clip + single-child-world composite. Not part of the Cosmic Reef pivot; append-only
        // addition to this file, staged independently of the uncommitted Cosmic Reef hunks above.
        public static readonly SceneDefinition HybridTest = new()
        {
            Id             = "HybridTest",
            DisplayName    = "Hybrid Test (prototype)",
            Description    = "Phase 1 hybrid-video proof: one hardcoded VisionBoard clip composited with Stellar Nursery via an adjustable blend. Prototype only - not a finished scene.",
            Status         = "Prototype v0.1 (Phase 1 proof)",
            DefaultProfile = "Safe",
            Showable       = true,
            ThumbnailPath  = null,
            ShowSeed       = null,
            Factory        = camera => new HybridTestScene(camera)
        };

        /// <summary>Every registered scene, in display order. First entry is the default.</summary>
        public static readonly SceneDefinition[] All = { StellarNursery, LavaLamp, WindTurbineFire, Underwater, HybridTest };

        public const string DefaultSceneId = "StellarNursery";

        /// <summary>Case-insensitive lookup by Id. On failure, `scene` is the default (StellarNursery) and this returns false - never throws, mirrors PerformanceProfile.TryParse's fallback pattern.</summary>
        public static bool TryParse(string id, out SceneDefinition scene)
        {
            foreach (var s in All)
            {
                if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    scene = s;
                    return true;
                }
            }
            scene = All[0];
            return false;
        }
    }
}
