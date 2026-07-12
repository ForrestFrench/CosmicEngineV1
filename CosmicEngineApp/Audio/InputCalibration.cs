using System;

namespace CosmicEngine.App.Audio
{
    /// <summary>
    /// Per-input (A or B) calibration state and response-curve model for the
    /// Dashboard Calibration Tab (v0.1). Deliberately separate from Tuning.cs
    /// (which drives Stellar Nursery's own floor/max calibration) and from any
    /// scene-specific shader tuning: this answers "how loud is this input, and
    /// how do we map that to a normalized 0-1 visual-control value", not "how
    /// should a specific scene react to that value". Affects only the
    /// calibration/visual-control signal computed here - never the actual
    /// captured audio.
    /// </summary>
    public class InputCalibration
    {
        // --- Manual calibration controls ---
        public float Gain = 1.0f;           // multiplies raw level before the gate/curve
        public float GateThreshold = 0.02f; // gated level below this reads as 0 (noise gate)
        public float Smoothing = 0.6f;      // exponential lerp factor, same style as Tuning.Smoothing
        public float OutputCeiling = 1.0f;  // post-curve output is clamped to this

        // --- Response curve: cubic bezier from (0,0) to (1,1) via two draggable
        // control points (P1, P2), the same model CSS cubic-bezier() easing and
        // eDRUMin's two-point curve editor use. ---
        public float CurveP1X = 0.33f, CurveP1Y = 0.33f;
        public float CurveP2X = 0.66f, CurveP2Y = 0.66f;
        public string Preset = "Linear";

        // --- Live computed values, updated once per CalibrationEngine tick ---
        public float RawLevel;
        public float SmoothedLevel;
        public float PeakLevel;
        public float PeakHoldLevel;
        public bool Clipping;
        public float CurveOutput;

        // Calibrated Audio Reactivity Integration v0.1: an explicit, clearly-labeled
        // test-injection value (see CalibrationEngine.SetTestOverride). When set, the
        // CalibrationEngine tick feeds this value in as the raw input level instead of
        // reading AudioEngine, so the full real gain/gate/curve/smoothing/peak pipeline
        // and downstream scene blending can be exercised and proven end-to-end without
        // a real guitar/interface connected. Never set by any normal code path - only
        // by an explicit dashboard/test call. Always null in real play.
        public float? TestOverrideRawLevel;

        private float _peakHoldTimer;

        private const float ClipThreshold = 0.98f;
        private const float PeakHoldSeconds = 1.5f;
        private const float PeakDecayPerSecond = 0.6f;

        public void Update(float rawInputLevel, float deltaSeconds)
        {
            RawLevel = rawInputLevel;

            // Gain + noise gate, applied to a working value only - never touches
            // the actual captured audio (AudioEngine.Guitar1/Guitar2 are untouched).
            float gated = rawInputLevel * Gain;
            if (gated < GateThreshold) gated = 0f;

            Clipping = gated >= ClipThreshold;
            gated = MathF.Min(gated, 1.5f); // sane upper bound before the curve

            SmoothedLevel = SmoothedLevel * Smoothing + gated * (1f - Smoothing);

            if (SmoothedLevel > PeakLevel)
                PeakLevel = SmoothedLevel;
            else
                PeakLevel = MathF.Max(0f, PeakLevel - PeakDecayPerSecond * deltaSeconds);

            if (SmoothedLevel >= PeakHoldLevel)
            {
                PeakHoldLevel = SmoothedLevel;
                _peakHoldTimer = PeakHoldSeconds;
            }
            else if (_peakHoldTimer > 0f)
            {
                _peakHoldTimer -= deltaSeconds;
            }
            else
            {
                PeakHoldLevel = MathF.Max(0f, PeakHoldLevel - PeakDecayPerSecond * deltaSeconds);
            }

            float x = MathF.Max(0f, MathF.Min(1f, SmoothedLevel));
            CurveOutput = MathF.Min(EvaluateCurve(x), OutputCeiling);
        }

        /// <summary>
        /// Evaluates the response curve at a given input x (0-1), returning the
        /// mapped output y (0-1). Solves for the bezier parameter t via a few
        /// Newton-Raphson iterations (bezierX(t) is monotonic for X-clamped
        /// control points, so this converges quickly and reliably) - the same
        /// approach CSS's cubic-bezier() timing functions use.
        /// </summary>
        public float EvaluateCurve(float x)
        {
            x = MathF.Max(0f, MathF.Min(1f, x));
            float t = x;
            for (int i = 0; i < 6; i++)
            {
                float xt = BezierComponent(t, CurveP1X, CurveP2X);
                float dx = BezierDerivative(t, CurveP1X, CurveP2X);
                if (MathF.Abs(dx) < 1e-5f) break;
                t -= (xt - x) / dx;
                t = MathF.Max(0f, MathF.Min(1f, t));
            }
            return BezierComponent(t, CurveP1Y, CurveP2Y);
        }

        private static float BezierComponent(float t, float p1, float p2)
        {
            float mt = 1f - t;
            return 3f * mt * mt * t * p1 + 3f * mt * t * t * p2 + t * t * t;
        }

        private static float BezierDerivative(float t, float p1, float p2)
        {
            float mt = 1f - t;
            return 3f * mt * mt * p1 + 6f * mt * t * (p2 - p1) + 3f * t * t * (1f - p2);
        }

        public void ApplyPreset(string preset)
        {
            switch (preset)
            {
                case "Linear":
                    CurveP1X = 0.33f; CurveP1Y = 0.33f; CurveP2X = 0.66f; CurveP2Y = 0.66f;
                    Preset = "Linear";
                    break;
                case "Sensitive":
                    // Bows toward the top-left: quiet input already produces a
                    // meaningfully higher output - for players who don't hit hard,
                    // avoids the visual sitting near-off most of the time.
                    CurveP1X = 0.15f; CurveP1Y = 0.55f; CurveP2X = 0.50f; CurveP2Y = 0.90f;
                    Preset = "Sensitive";
                    break;
                case "Compressed":
                    // Bows toward the bottom-right: needs more input before output
                    // ramps up, then eases the top so hard hits don't all pin to
                    // 1.0 - flattens dynamics instead of a hard on/off jump.
                    CurveP1X = 0.55f; CurveP1Y = 0.15f; CurveP2X = 0.85f; CurveP2Y = 0.55f;
                    Preset = "Compressed";
                    break;
                case "S-Curve":
                    // Emphasizes the middle range, de-emphasizes near-silent noise
                    // and near-max jitter - the classic deadzone+ramp curve shape.
                    CurveP1X = 0.20f; CurveP1Y = 0.05f; CurveP2X = 0.80f; CurveP2Y = 0.95f;
                    Preset = "S-Curve";
                    break;
                default:
                    // Unknown preset name: leave curve points as-is, just label it.
                    Preset = "Custom";
                    break;
            }
        }

        public void SetCustomCurve(float p1x, float p1y, float p2x, float p2y)
        {
            CurveP1X = Clamp01(p1x); CurveP1Y = Clamp01(p1y);
            CurveP2X = Clamp01(p2x); CurveP2Y = Clamp01(p2y);
            Preset = "Custom";
        }

        public void ResetDefaults()
        {
            Gain = 1.0f;
            GateThreshold = 0.02f;
            Smoothing = 0.6f;
            OutputCeiling = 1.0f;
            ApplyPreset("Linear");
            RawLevel = SmoothedLevel = PeakLevel = PeakHoldLevel = CurveOutput = 0f;
            Clipping = false;
            _peakHoldTimer = 0f;
            TestOverrideRawLevel = null;
        }

        private static float Clamp01(float v) => MathF.Max(0f, MathF.Min(1f, v));
    }
}
