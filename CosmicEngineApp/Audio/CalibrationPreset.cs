using System;

namespace CosmicEngine.App.Audio
{
    /// <summary>
    /// Calibration Presets v0.1: a full, named snapshot of everything needed to
    /// recreate a usable input response - routing (which physical channel drives
    /// Input A/B) plus each input's manual controls and response curve. Deliberately
    /// whole-setup, not per-input, since a real switch (e.g. Clarett practice rig
    /// vs. Scarlett live rig) usually means both channel assignments and both
    /// curves change together.
    ///
    /// This is a plain data/logic class, not a static singleton like
    /// CalibrationEngine - CalibrationPresetStore owns the actual list of saved
    /// presets and the JSON file they persist to.
    /// </summary>
    public class CalibrationPreset
    {
        public string Name { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string Notes { get; set; } = "";

        // "Clarett", "Scarlett", or "Generic" - a hint only, never enforced. Even
        // though true multi-channel Clarett capture isn't implemented yet (see
        // AUDIT.md Entry 26/27), presets are designed with that future in mind:
        // a "Clarett" preset can already store channel indices beyond what the
        // current 2-channel backend exposes, so it's ready to mean something real
        // once multi-channel capture lands, without a preset-format migration.
        public string TargetInterface { get; set; } = "Generic";

        public int ChannelA { get; set; }
        public int ChannelB { get; set; }

        public InputPresetData InputA { get; set; } = new InputPresetData();
        public InputPresetData InputB { get; set; } = new InputPresetData();

        // Informational only, captured at save time - never validated or relied on
        // for correctness. AudioEngine exposes no device-name API (see
        // CalibrationEngine.cs), so this is always null today; the field exists so
        // a future device-name API doesn't require another preset-format migration.
        public string? AudioDeviceName { get; set; }
        public int ChannelCountAtSave { get; set; }

        public class InputPresetData
        {
            public float Gain { get; set; } = 1.0f;
            public float GateThreshold { get; set; } = 0.02f;
            public float Smoothing { get; set; } = 0.6f;
            public float OutputCeiling { get; set; } = 1.0f;
            public float CurveP1X { get; set; } = 0.33f;
            public float CurveP1Y { get; set; } = 0.33f;
            public float CurveP2X { get; set; } = 0.66f;
            public float CurveP2Y { get; set; } = 0.66f;
            public string CurvePreset { get; set; } = "Linear";

            public static InputPresetData CaptureFrom(InputCalibration c) => new InputPresetData
            {
                Gain = c.Gain,
                GateThreshold = c.GateThreshold,
                Smoothing = c.Smoothing,
                OutputCeiling = c.OutputCeiling,
                CurveP1X = c.CurveP1X,
                CurveP1Y = c.CurveP1Y,
                CurveP2X = c.CurveP2X,
                CurveP2Y = c.CurveP2Y,
                CurvePreset = c.Preset
            };

            /// <summary>
            /// Applies this saved data onto a live InputCalibration instance,
            /// clamping every numeric field to the same valid ranges the dashboard's
            /// own sliders enforce (see ControlServer.cs's gain/gate/smoothing/ceil
            /// slider min/max) - a corrupt or hand-edited preset file can never push
            /// a scene's audio-reactive input outside a safe, already-tested range.
            /// </summary>
            public void ApplyTo(InputCalibration c)
            {
                c.Gain = Clamp(Gain, 0f, 4f);
                c.GateThreshold = Clamp(GateThreshold, 0f, 0.3f);
                c.Smoothing = Clamp(Smoothing, 0f, 0.95f);
                c.OutputCeiling = Clamp(OutputCeiling, 0.1f, 1f);
                c.SetCustomCurve(CurveP1X, CurveP1Y, CurveP2X, CurveP2Y);
                // SetCustomCurve always labels the result "Custom" - if the saved
                // curve was actually one of the named presets, re-apply it by name
                // instead so the curve-editor UI highlights the right preset button
                // and the exact canonical points are restored (avoids any drift
                // from float round-tripping through JSON).
                if (CurvePreset is "Linear" or "Sensitive" or "Compressed" or "S-Curve")
                    c.ApplyPreset(CurvePreset);
            }

            private static float Clamp(float v, float min, float max) =>
                MathF.Max(min, MathF.Min(max, v));
        }

        /// <summary>
        /// Captures a full preset from CalibrationEngine's current live state.
        /// </summary>
        public static CalibrationPreset CaptureCurrent(string name, string notes, string targetInterface)
        {
            string now = DateTime.UtcNow.ToString("o");
            return new CalibrationPreset
            {
                Name = name,
                CreatedAt = now,
                UpdatedAt = now,
                Notes = notes ?? "",
                TargetInterface = string.IsNullOrWhiteSpace(targetInterface) ? "Generic" : targetInterface,
                ChannelA = CalibrationEngine.ChannelA,
                ChannelB = CalibrationEngine.ChannelB,
                InputA = InputPresetData.CaptureFrom(CalibrationEngine.InputA),
                InputB = InputPresetData.CaptureFrom(CalibrationEngine.InputB),
                AudioDeviceName = null, // no device-name API exists yet, see CalibrationEngine.cs
                ChannelCountAtSave = CalibrationEngine.ChannelCount
            };
        }

        /// <summary>
        /// Applies this preset onto the live CalibrationEngine state. Returns a
        /// warning string (empty if none) - e.g. when the preset's saved channel
        /// indices are out of range for the currently-available capture backend
        /// (the exact "saved a Clarett preset, now running on a Scarlett" case the
        /// brief calls out). In that case channel routing is deliberately left
        /// unchanged (not clamped to some arbitrary in-range value) since the
        /// currently-selected channel is a safer fallback than guessing.
        /// </summary>
        public string ApplyTo()
        {
            string warning = "";
            if (ChannelA >= 0 && ChannelA < CalibrationEngine.ChannelCount &&
                ChannelB >= 0 && ChannelB < CalibrationEngine.ChannelCount)
            {
                CalibrationEngine.SetChannels(ChannelA, ChannelB);
            }
            else
            {
                warning = $"Preset '{Name}' references channel(s) not available on this backend " +
                          $"(saved for {ChannelCountAtSave}-channel capture; only {CalibrationEngine.ChannelCount} " +
                          "available now) - input routing left unchanged, only levels/curves were applied.";
            }

            InputA.ApplyTo(CalibrationEngine.InputA);
            InputB.ApplyTo(CalibrationEngine.InputB);
            return warning;
        }
    }
}
