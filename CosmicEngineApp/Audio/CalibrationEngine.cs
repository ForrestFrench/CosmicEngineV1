using System;
using System.Threading;

namespace CosmicEngine.App.Audio
{
    /// <summary>
    /// Dashboard Calibration Tab v0.1 backend: selects which two of the currently
    /// available audio channels ("Input A"/"Input B") drive the calibration
    /// meters/curves, and ticks their smoothing/peak-hold/curve state on a
    /// lightweight background timer so meters look live even when polled
    /// infrequently by the dashboard.
    ///
    /// Self-starting by design (see the static constructor below): no explicit
    /// Start()/Stop() wiring was added to CosmicEngine.cs/DashboardHost.cs, so no
    /// scene or launcher file needs to know this class exists to keep it ticking.
    /// A static constructor (not a lazy EnsureStarted() check, as in the original
    /// v0.1 design) is required as of Calibrated Audio Reactivity Integration v0.1:
    /// scenes now read CalibrationEngine.InputA/InputB directly every frame, and a
    /// scene doing so would never have called SetChannels()/Snapshot() (the only
    /// two places the old lazy check lived), so the timer could stay parked at
    /// zero forever. A static constructor is guaranteed by the CLR to run before
    /// the first access to any static member of this type, so simply referencing
    /// CalibrationEngine.InputA from a scene's Render() is now sufficient. One
    /// consequence, accepted and documented rather than hidden: the background
    /// timer now also starts during bounded --smoke-test/--diagnostic runs (any
    /// world's Update()/Render() touches CalibrationEngine), not just when the
    /// dashboard's calibration endpoints are hit. This is inexpensive (cheap
    /// float-only work at ~30Hz) and harmless - the backing System.Threading.Timer
    /// runs its callback on a ThreadPool thread, which does not keep the process
    /// alive on its own, consistent with every bounded mode's existing
    /// Environment.Exit(0)-on-unload guarantee.
    ///
    /// Channel-count honesty: AudioEngine currently opens a single stereo OpenAL
    /// capture device (see AudioEngine.cs) - so exactly 2 channels are ever really
    /// available (Guitar1 = physical Input 1/Left, Guitar2 = physical Input
    /// 2/Right), regardless of how many inputs the connected interface (e.g. an
    /// 8-input Clarett) actually has. ChannelCount/ChannelNames below reflect that
    /// real, current capability, not the interface's full input count. True
    /// per-channel multi-input capture (ASIO/CoreAudio device selection instead of
    /// stereo OpenAL) is out of scope for this pass - see AUDIT.md/ROADMAP.md for
    /// the documented v0.2/v0.3 follow-up.
    /// </summary>
    public static class CalibrationEngine
    {
        public const int ChannelCount = 2;
        public static readonly string[] ChannelNames = { "Input 1 (Left)", "Input 2 (Right)" };

        public static int ChannelA { get; private set; } = 0;
        public static int ChannelB { get; private set; } = 1;

        public static readonly InputCalibration InputA = new InputCalibration();
        public static readonly InputCalibration InputB = new InputCalibration();

        // Calibrated Audio Reactivity Integration v0.1: shared, modest blend weight
        // scenes use when adding a calibrated post-curve value on top of their own
        // existing raw-audio-derived parameters (see StellarNursery.cs/
        // LavaLampScene.cs). A single source of truth so both scenes stay in sync
        // and a future tuning pass only needs to change one number. Deliberately
        // small - this is an additive nudge toward smoother response, not a
        // replacement of each scene's existing (already-tuned) audio path.
        public const float CalibratedBlendWeight = 0.3f;

        private static readonly Timer _timer;
        private static long _lastTicks;

        static CalibrationEngine()
        {
            _lastTicks = DateTime.UtcNow.Ticks;
            _timer = new Timer(Tick, null, 0, 33); // ~30Hz, cheap float-only work
        }

        public static void SetChannels(int a, int b)
        {
            ChannelA = ClampChannel(a);
            ChannelB = ClampChannel(b);
        }

        private static int ClampChannel(int ch) =>
            ch < 0 ? 0 : (ch >= ChannelCount ? ChannelCount - 1 : ch);

        private static float RawLevelFor(int channel) =>
            channel == 0 ? AudioEngine.Guitar1.Level : AudioEngine.Guitar2.Level;

        private static void Tick(object? state)
        {
            long now = DateTime.UtcNow.Ticks;
            float dt = (float)((now - _lastTicks) / (double)TimeSpan.TicksPerSecond);
            _lastTicks = now;
            if (dt <= 0f || dt > 1f) dt = 1f / 30f; // guard against clock jumps / first tick

            // Test-injection override (see SetTestOverride) takes priority over the
            // real captured level when set - lets the full real pipeline (gain,
            // gate, curve, smoothing, peak, and downstream scene blending) be
            // exercised and proven without a real guitar/interface connected.
            // Always null in real play; never set by any normal code path.
            float rawA = InputA.TestOverrideRawLevel ?? RawLevelFor(ChannelA);
            float rawB = InputB.TestOverrideRawLevel ?? RawLevelFor(ChannelB);

            InputA.Update(rawA, dt);
            InputB.Update(rawB, dt);
        }

        /// <summary>
        /// Sets or clears a clearly-labeled test-injection raw level for one or
        /// both inputs (see InputCalibration.TestOverrideRawLevel). Pass null to
        /// clear and resume reading real captured audio. Only ever called from an
        /// explicit dashboard "test pulse" action - never automatically.
        /// </summary>
        public static void SetTestOverride(string? input, float? value)
        {
            if (string.Equals(input, "Both", StringComparison.OrdinalIgnoreCase))
            {
                InputA.TestOverrideRawLevel = value;
                InputB.TestOverrideRawLevel = value;
            }
            else
            {
                ResolveInput(input).TestOverrideRawLevel = value;
            }
        }

        public static object Snapshot() => new
        {
            channelCount = ChannelCount,
            channelNames = ChannelNames,
            channelA = ChannelA,
            channelB = ChannelB,
            audioCapturing = AudioEngine.IsCapturing,
            inputA = SnapshotOf(InputA),
            inputB = SnapshotOf(InputB)
        };

        private static object SnapshotOf(InputCalibration c) => new
        {
            gain = c.Gain,
            gateThreshold = c.GateThreshold,
            smoothing = c.Smoothing,
            outputCeiling = c.OutputCeiling,
            preset = c.Preset,
            curveP1X = c.CurveP1X,
            curveP1Y = c.CurveP1Y,
            curveP2X = c.CurveP2X,
            curveP2Y = c.CurveP2Y,
            rawLevel = c.RawLevel,
            smoothedLevel = c.SmoothedLevel,
            peakLevel = c.PeakLevel,
            peakHoldLevel = c.PeakHoldLevel,
            clipping = c.Clipping,
            curveOutput = c.CurveOutput,
            testOverrideActive = c.TestOverrideRawLevel.HasValue
        };

        public static InputCalibration ResolveInput(string? key) =>
            string.Equals(key, "B", StringComparison.OrdinalIgnoreCase) ? InputB : InputA;
    }
}
