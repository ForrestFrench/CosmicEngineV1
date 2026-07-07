using CosmicEngine.App.Worlds.World01;
using CosmicEngine.App.Worlds.World02;
using System;

namespace CosmicEngine.App.Engine
{
    /// <summary>
    /// Lava Lamp Scene Draft v0.1: minimal world-selection support so the engine can
    /// run more than one IWorld. StellarNursery remains the default (unchanged from
    /// prior behavior - normal `dotnet run` with no --world flag is identical to
    /// before this pass) - LavaLamp must be explicitly requested via --world LavaLamp.
    /// </summary>
    public static class WorldSelector
    {
        public const string DefaultWorldName = "StellarNursery";
        public static readonly string[] ValidNames = { "StellarNursery", "LavaLamp" };
        public static readonly string ValidNamesList = "StellarNursery, LavaLamp";

        /// <summary>Case-insensitive match against the known world names. On failure, `resolved` is the default.</summary>
        public static bool TryParse(string name, out string resolved)
        {
            foreach (var n in ValidNames)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = n;
                    return true;
                }
            }
            resolved = DefaultWorldName;
            return false;
        }

        public static IWorld Create(string name, Camera camera) => name switch
        {
            "LavaLamp" => new LavaLampScene(camera),
            _          => new StellarNursery(camera),
        };
    }
}
