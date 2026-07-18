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

        // World05 Hybrid Test (Phase 1 "Hybrid Proof", video-atoms milestone): the single blend
        // uniform for HybridTestScene's mix(video, child, uBlend) composite. 0 = pure video layer,
        // 1 = pure procedural child world, 0.5 = even mix. Clamped defensively to [0,1] both here
        // and again in HybridTestScene.Render(), matching the WindTurbineFireEvolutionSeconds/
        // UnderwaterEvolutionSeconds defensive-clamp pattern. Harmless no-op for every other scene.
        public static float HybridBlend = 0.5f;

        // Phase 3 Effect Stack v1 (MILESTONE_BREAKDOWN.md Phase 3, video-atoms). All World05
        // HybridTest-only, harmless no-ops for every other scene - same convention as
        // WindTurbineFireEvolutionSeconds/UnderwaterEvolutionSeconds/HybridBlend above. Applied in
        // hybrid.frag AFTER the existing mix(video, child, uBlend), which is unchanged.
        public static float HybridGrayscale = 0.0f;   // 0 = full color, 1 = full luminance
        public static bool  HybridMirrorX = false;
        public static bool  HybridMirrorY = false;
        public static float HybridGradeLift = 0.0f;   // -0.5..0.5
        public static float HybridGradeGamma = 1.0f;  // 0.2..3.0
        public static float HybridGradeGain = 1.0f;   // 0..2
        public static float HybridVignette = 0.0f;    // 0..1
        // Decode-rate pacing multiplier (FfmpegPipeDecoder reader thread), NOT a shader uniform -
        // per VIDEO_SYSTEM_ARCHITECTURE.md §2.2, playback speed lives in the decoder, not the
        // composite shader. 0.25x-2x, defensively clamped again at the decoder.
        public static float HybridPlaybackSpeed = 1.0f;

        // World06 Visual Composer Sandbox (artistic-exploration pass, not a phased-roadmap
        // milestone - see AUDIT.md). Same "Tuning field -> ControlServer /set + /values -> HTML
        // slider" convention as every field above; all World06-only, harmless no-ops for every
        // other scene. ComposerIndex selects which of the hardcoded compositions is active
        // (VisualComposerScene.Compositions[]), so the dashboard can cycle compositions without a
        // recompile.
        public static int   ComposerIndex = 0;
        public static float ComposerBlend = 0.5f;              // 0 = pure video, 1 = pure procedural layer
        public static float ComposerVolumetricDensity = 1.0f;  // 0..2, scales the raymarched/volumetric child layer
        public static float ComposerParticleDensity = 1.0f;    // 0..2, scales the inline particle/motes field
        public static float ComposerLighting = 1.0f;           // 0..2, scales the independent raking-light layer
        public static float ComposerAudioReactivity = 1.0f;    // 0..2, scales the additive calibrated-audio nudge
        public static float ComposerGrayscale = 0.0f;
        public static bool  ComposerMirrorX = false;
        public static bool  ComposerMirrorY = false;
        public static float ComposerGradeLift = 0.0f;
        public static float ComposerGradeGamma = 1.0f;
        public static float ComposerGradeGain = 1.0f;
        public static float ComposerVignette = 0.0f;
        public static float ComposerPlaybackSpeed = 1.0f;
    }
}
