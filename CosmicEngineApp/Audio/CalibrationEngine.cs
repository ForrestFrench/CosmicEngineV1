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
    /// Self-starting by design (see EnsureStarted): no explicit Start()/Stop()
    /// wiring was added to CosmicEngine.cs/DashboardHost.cs on purpose, to avoid
    /// touching those files for a dashboard-only feature and to guarantee bounded
    /// diagnostic runs (--smoke-test etc., which never call any /calibration/*
    /// route) are completely unaffected. The backing System.Threading.Timer runs
    /// its callback on a ThreadPool thread, which does not keep the process alive
    /// on its own - consistent with every bounded mode's existing
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

        private static Timer? _timer;
        private static readonly object _startLock = new object();
        private static bool _started;
        private static long _lastTicks;

        private static void EnsureStarted()
        {
            if (_started) return;
            lock (_startLock)
            {
                if (_started) return;
                _started = true;
                _lastTicks = DateTime.UtcNow.Ticks;
                _timer = new Timer(Tick, null, 0, 33); // ~30Hz, cheap float-only work
            }
        }

        public static void SetChannels(int a, int b)
        {
            EnsureStarted();
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

            InputA.Update(RawLevelFor(ChannelA), dt);
            InputB.Update(RawLevelFor(ChannelB), dt);
        }

        public static object Snapshot()
        {
            EnsureStarted();
            return new
            {
                channelCount = ChannelCount,
                channelNames = ChannelNames,
                channelA = ChannelA,
                channelB = ChannelB,
                audioCapturing = AudioEngine.IsCapturing,
                inputA = SnapshotOf(InputA),
                inputB = SnapshotOf(InputB)
            };
        }

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
            curveOutput = c.CurveOutput
        };

        public static InputCalibration ResolveInput(string? key) =>
            string.Equals(key, "B", StringComparison.OrdinalIgnoreCase) ? InputB : InputA;
    }
}
