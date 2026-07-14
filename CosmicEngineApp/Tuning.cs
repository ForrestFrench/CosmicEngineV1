namespace CosmicEngine.App
{
    public static class Tuning
    {
        // Audio calibration - these are your already-tuned values for the
        // Focusrite Clarett interface, carried over unchanged.
        public static float Smoothing = 0.40f;

        public static float BassFloor   = 0.08f;
        public static float MidFloor    = 0.004f;
        public static float TrebleFloor = 0.001f;

        public static float BassMax   = 35f;
        public static float MidMax    = 12f;
        public static float TrebleMax = 0.4f;

        // Overall brightness response.
        public static float BassBrightness = 1.20f;

        // Ambient floor under silence (Visual Recovery Pass 1: raised from 0.20).
        // This multiplies the whole composited frame before gamma; at 0.20 the
        // scene was visibly too dark with no audio input. 0.45 keeps plenty of
        // headroom for uBassBrightness to still brighten further with live audio.
        public static float DimLevel       = 0.55f;

        // World03 Wind Turbine Fire (Phase 1.1): how many seconds of sustained
        // full fire-drive it takes uSceneHeat to reach fully "hot" - lets the
        // user match the scene's evolution to a song's actual length via the
        // dashboard slider (WindTurbineFireScene.Update() reads this every
        // frame, clamped defensively to [30,300] even if something outside the
        // slider's own clamping sets it out of range). Default 240s (4 min)
        // matches the original hardcoded Phase 1 value - no behavior change
        // unless the user moves the slider. Harmless no-op for every other
        // scene, same as LavaLampScene.BlobCount/WindTurbineFireScene.EmberCount.
        public static float WindTurbineFireEvolutionSeconds = 240f;

        // World04 Underwater (Phase 1): how many seconds of sustained light
        // drive it takes uBloom to reach fully "bloomed" - lets the user
        // match the scene's evolution to a song's actual length via the
        // dashboard slider (UnderwaterScene.Update() reads this every frame,
        // clamped defensively to [30,300] even if something outside the
        // slider's own clamping sets it out of range). Default 240s (4 min),
        // same default as WindTurbineFireEvolutionSeconds. Harmless no-op
        // for every other scene, same as WindTurbineFireEvolutionSeconds/
        // WindTurbineFireScene.EmberCount.
        public static float UnderwaterEvolutionSeconds = 240f;
    }
}
