using CosmicEngine.App;
using System;
using System.Diagnostics;

namespace CosmicEngine.App.Audio
{
    /// <summary>
    /// Thin musical-intent response layer over the existing AudioEngine and
    /// CalibrationEngine. It owns no capture device and performs no FFT.
    /// Snapshot() is advanced only when a read-only consumer requests data.
    /// </summary>
    public static class GuitarIntentResponse
    {
        private sealed class ChannelState
        {
            public float PreviousIntensity;
            public float Bass;
            public float Mid;
            public float Treble;
            public float Attack;
            public float Sustain;
            public float ActiveSeconds;
            public float QuietSeconds;
            public bool Silent = true;
        }

        private static readonly object Sync = new object();
        private static readonly ChannelState StateA = new ChannelState();
        private static readonly ChannelState StateB = new ChannelState();
        private static long _lastTimestamp = Stopwatch.GetTimestamp();
        private static long _sequence;
        private static float _interaction;

        public static GuitarIntentSnapshot Snapshot()
        {
            lock (Sync)
            {
                long now = Stopwatch.GetTimestamp();
                float dt = (float)((now - _lastTimestamp) / (double)Stopwatch.Frequency);
                _lastTimestamp = now;
                dt = Math.Clamp(dt, 1f / 240f, 0.25f);

                bool captureActive = AudioEngine.IsCapturing;
                var rawA = ChannelFor(CalibrationEngine.ChannelA);
                var rawB = ChannelFor(CalibrationEngine.ChannelB);

                float intensityA = captureActive ? Clamp01(CalibrationEngine.InputA.CurveOutput) : 0f;
                float intensityB = captureActive ? Clamp01(CalibrationEngine.InputB.CurveOutput) : 0f;

                GuitarIntentChannel guitarA = UpdateChannel(StateA, rawA, intensityA, captureActive, dt);
                GuitarIntentChannel guitarB = UpdateChannel(StateB, rawB, intensityB, captureActive, dt);

                float interactionTarget = MathF.Sqrt(guitarA.Sustain * guitarB.Sustain);
                _interaction = Smooth(_interaction, interactionTarget, dt,
                    interactionTarget > _interaction ? 0.25f : 0.8f);

                return new GuitarIntentSnapshot
                {
                    Sequence = ++_sequence,
                    CapturedAtMonotonicMs = now * 1000.0 / Stopwatch.Frequency,
                    CaptureActive = captureActive,
                    GuitarA = guitarA,
                    GuitarB = guitarB,
                    Interaction = Clamp01(_interaction)
                };
            }
        }

        private static GuitarIntentChannel UpdateChannel(
            ChannelState state,
            GuitarChannel raw,
            float intensity,
            bool captureActive,
            float dt)
        {
            float bassTarget = captureActive ? NormalizeBand(raw.Bass, Tuning.BassFloor, Tuning.BassMax) : 0f;
            float midTarget = captureActive ? NormalizeBand(raw.Mid, Tuning.MidFloor, Tuning.MidMax) : 0f;
            float trebleTarget = captureActive ? NormalizeBand(raw.Treble, Tuning.TrebleFloor, Tuning.TrebleMax) : 0f;

            state.Bass = Smooth(state.Bass, bassTarget, dt, 0.18f);
            state.Mid = Smooth(state.Mid, midTarget, dt, 0.18f);
            state.Treble = Smooth(state.Treble, trebleTarget, dt, 0.14f);

            float positiveDelta = MathF.Max(0f, intensity - state.PreviousIntensity);
            float attackTarget = Clamp01(positiveDelta * 5f + state.Treble * positiveDelta * 2f);
            state.Attack = attackTarget > state.Attack
                ? attackTarget
                : Smooth(state.Attack, 0f, dt, 0.22f);
            state.PreviousIntensity = intensity;

            if (captureActive && intensity >= 0.08f)
                state.ActiveSeconds = MathF.Min(10f, state.ActiveSeconds + dt);
            else if (intensity < 0.05f)
                state.ActiveSeconds = 0f;

            float sustainTarget = state.ActiveSeconds >= 0.25f ? intensity : 0f;
            state.Sustain = Smooth(state.Sustain, sustainTarget, dt,
                sustainTarget > state.Sustain ? 0.35f : 1.2f);

            if (!captureActive)
            {
                state.QuietSeconds = 1f;
                state.Silent = true;
            }
            else if (intensity <= 0.025f)
            {
                state.QuietSeconds += dt;
                if (state.QuietSeconds >= 0.35f)
                    state.Silent = true;
            }
            else if (intensity >= 0.05f)
            {
                state.QuietSeconds = 0f;
                state.Silent = false;
            }

            return new GuitarIntentChannel
            {
                InputLevel = intensity,
                RmsEnergy = captureActive ? Clamp01(raw.Level) : 0f,
                Intensity = intensity,
                Bass = Clamp01(state.Bass),
                Mid = Clamp01(state.Mid),
                Treble = Clamp01(state.Treble),
                Attack = Clamp01(state.Attack),
                Sustain = Clamp01(state.Sustain),
                Silent = state.Silent
            };
        }

        private static GuitarChannel ChannelFor(int channel) =>
            channel == 0 ? AudioEngine.Guitar1 : AudioEngine.Guitar2;

        private static float NormalizeBand(float value, float floor, float maximum)
        {
            if (maximum <= floor)
                return 0f;
            return Clamp01((value - floor) / (maximum - floor));
        }

        private static float Smooth(float current, float target, float dt, float timeConstant)
        {
            float blend = 1f - MathF.Exp(-dt / MathF.Max(0.001f, timeConstant));
            return current + (target - current) * blend;
        }

        private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
    }
}
