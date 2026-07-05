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
        public static float DimLevel       = 0.20f;
    }
}
