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
    }
}
