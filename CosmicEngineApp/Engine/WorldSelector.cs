using System;

namespace CosmicEngine.App.Engine
{
    /// <summary>
    /// Lava Lamp Scene Draft v0.1: minimal world-selection support so the engine can
    /// run more than one IWorld. StellarNursery remains the default (unchanged from
    /// prior behavior - normal `dotnet run` with no --world flag is identical to
    /// before this pass) - LavaLamp must be explicitly requested via --world LavaLamp.
    ///
    /// Scene Dashboard v0.1: this is now a thin wrapper over SceneRegistry, the single
    /// source of truth for scene metadata used by both --world CLI parsing and the
    /// dashboard's scene cards. Kept as its own type (rather than inlining into
    /// CosmicEngine.cs/PerformanceSweep.cs) so those call sites and their existing
    /// --world/warning-fallback behavior did not need to change.
    /// </summary>
    public static class WorldSelector
    {
        public const string DefaultWorldName = SceneRegistry.DefaultSceneId;

        public static readonly string[] ValidNames =
            Array.ConvertAll(SceneRegistry.All, s => s.Id);

        public static readonly string ValidNamesList = string.Join(", ", ValidNames);

        /// <summary>Case-insensitive match against the known world names. On failure, `resolved` is the default.</summary>
        public static bool TryParse(string name, out string resolved)
        {
            bool found = SceneRegistry.TryParse(name, out var scene);
            resolved = scene.Id;
            return found;
        }

        public static IWorld Create(string name, Camera camera)
        {
            SceneRegistry.TryParse(name, out var scene);
            return scene.Factory(camera);
        }
    }
}
