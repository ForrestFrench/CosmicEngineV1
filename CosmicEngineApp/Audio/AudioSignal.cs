namespace CosmicEngine.App.Audio
{
    /// <summary>
    /// Per-frame snapshot of both guitar signals.
    /// Passed by value into every world's Update() call.
    /// Guitar 1 = Creator (energy, color, ignition).
    /// Guitar 2 = Sculptor (gravity, structure, motion).
    /// </summary>
    public struct AudioSignal
    {
        public float Bass1;
        public float Mid1;
        public float Treble1;
        public float Level1;

        public float Bass2;
        public float Mid2;
        public float Treble2;
        public float Level2;

        /// <summary>Max of both bass signals — drives overall scene brightness.</summary>
        public float BassCombined => MathF.Max(Bass1, Bass2);
    }
}
