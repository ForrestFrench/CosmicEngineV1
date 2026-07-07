using CosmicEngine.App.Worlds.World01;
using CosmicEngine.App.Worlds.World02;
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

        /// <summary>Every registered scene, in display order. First entry is the default.</summary>
        public static readonly SceneDefinition[] All = { StellarNursery, LavaLamp };

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
