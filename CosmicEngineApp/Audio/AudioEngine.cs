using System;
using System.Collections.Generic;
using System.Threading;
using OpenTK.Audio.OpenAL;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace CosmicEngine.App.Audio
{
    public class GuitarChannel
    {
        public volatile float Level;
        public volatile float Bass;
        public volatile float Mid;
        public volatile float Treble;
    }

    public static class AudioEngine
    {
        private static ALCaptureDevice _device;
        private static Thread? _thread;
        private static volatile bool _running;

        /// <summary>True once the capture device is open and the capture thread is running.</summary>
        public static bool IsCapturing => _running;

        // Left channel (physical Input 1) and right channel (physical Input 2).
        public static readonly GuitarChannel Guitar1 = new GuitarChannel();
        public static readonly GuitarChannel Guitar2 = new GuitarChannel();

        private const int SampleRate = 44100;
        private const int BufferSize = 2048; // in sample FRAMES, not raw samples

        /// <summary>
        /// Name of the capture device actually opened, for diagnostics and for the
        /// dashboard/Media Console to display. Empty until <see cref="Start"/> succeeds.
        /// </summary>
        public static string OpenedDeviceName { get; private set; } = "";

        /// <summary>
        /// True once any sample above the noise floor has arrived since capture opened.
        /// This is what distinguishes "the guitarist isn't playing" from "we opened the
        /// wrong device and this stream is dead" - the two are otherwise identical from
        /// outside (capture_active true, all values 0), which is exactly how the
        /// "Hue Sync Audio" misbinding stayed invisible through several sessions.
        /// </summary>
        public static bool HasSeenSignal { get; internal set; }

        /// <summary>
        /// Virtual/loopback/aggregate devices that are never a guitar interface. macOS
        /// happily makes one of these the OpenAL default, and OpenAL's default does NOT
        /// track the CoreAudio default you set in System Settings - so "my input is set
        /// correctly" and "the app is reading silence" are entirely compatible.
        /// </summary>
        private static readonly string[] VirtualDeviceMarkers =
        {
            "hue sync", "soundflower", "blackhole", "loopback", "vb-cable", "vb cable",
            "zoomaudio", "krisp", "teams", "obs virtual", "ndi", "aggregate", "multi-output"
        };

        private static bool LooksVirtual(string name)
        {
            string n = name.ToLowerInvariant();
            foreach (var marker in VirtualDeviceMarkers)
                if (n.Contains(marker)) return true;
            return false;
        }

        /// <summary>
        /// Picks the capture device by name rather than trusting OpenAL's default.
        /// Order: COSMICENGINE_AUDIO_DEVICE (exact, then substring, case-insensitive)
        /// -> first enumerated device that does not look virtual -> OpenAL default.
        /// </summary>
        private static string? SelectDeviceName()
        {
            List<string> devices;
            try
            {
                devices = new List<string>(ALC.GetStringList(GetEnumerationStringList.CaptureDeviceSpecifier));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioEngine] Could not enumerate capture devices ({ex.Message}); falling back to the OpenAL default.");
                return null;
            }

            if (devices.Count == 0)
            {
                Console.WriteLine("[AudioEngine] No capture devices enumerated; falling back to the OpenAL default.");
                return null;
            }

            Console.WriteLine($"[AudioEngine] Capture devices visible to OpenAL ({devices.Count}):");
            foreach (var d in devices)
                Console.WriteLine($"[AudioEngine]   - \"{d}\"{(LooksVirtual(d) ? "   (looks virtual - skipped by auto-pick)" : "")}");

            string? requested = Environment.GetEnvironmentVariable("COSMICENGINE_AUDIO_DEVICE");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                foreach (var d in devices)
                    if (string.Equals(d, requested, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[AudioEngine] COSMICENGINE_AUDIO_DEVICE matched exactly: \"{d}\".");
                        return d;
                    }
                foreach (var d in devices)
                    if (d.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Console.WriteLine($"[AudioEngine] COSMICENGINE_AUDIO_DEVICE=\"{requested}\" matched \"{d}\".");
                        return d;
                    }
                Console.WriteLine($"[AudioEngine] WARNING: COSMICENGINE_AUDIO_DEVICE=\"{requested}\" matched no capture device. Falling through to auto-pick.");
            }

            foreach (var d in devices)
                if (!LooksVirtual(d))
                {
                    Console.WriteLine($"[AudioEngine] Auto-picked first non-virtual capture device: \"{d}\".");
                    return d;
                }

            Console.WriteLine("[AudioEngine] WARNING: every enumerated capture device looks virtual. Using the OpenAL default; expect silence.");
            return null;
        }

        public static void Start()
        {
            string? chosen = SelectDeviceName();

            _device = ALC.CaptureOpenDevice(chosen, SampleRate, ALFormat.Stereo16, BufferSize);

            if (_device == ALCaptureDevice.Null && chosen != null)
            {
                Console.WriteLine($"[AudioEngine] WARNING: could not open \"{chosen}\"; retrying with the OpenAL default device.");
                _device = ALC.CaptureOpenDevice(null, SampleRate, ALFormat.Stereo16, BufferSize);
            }

            if (_device == ALCaptureDevice.Null)
                throw new Exception("AudioEngine: could not open capture device. Check interface permissions.");

            HasSeenSignal = false;

            try
            {
                string opened = ALC.GetString(new ALDevice(_device.Handle), AlcGetString.CaptureDeviceSpecifier);
                OpenedDeviceName = opened ?? "";
                Console.WriteLine($"[AudioEngine] Capture opened: device=\"{opened}\", format=Stereo16, sampleRate={SampleRate}, bufferSize={BufferSize} frames.");
                if (!string.IsNullOrEmpty(opened) && LooksVirtual(opened))
                {
                    Console.WriteLine("[AudioEngine] ***********************************************************");
                    Console.WriteLine($"[AudioEngine] WARNING: \"{opened}\" is a virtual/loopback device, not a guitar");
                    Console.WriteLine("[AudioEngine] interface. Levels will read 0.000 no matter how loud you play.");
                    Console.WriteLine("[AudioEngine] Set COSMICENGINE_AUDIO_DEVICE to your interface name and restart.");
                    Console.WriteLine("[AudioEngine] ***********************************************************");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioEngine] Capture opened (device name readback failed, non-fatal: {ex.Message}).");
            }

            ALC.CaptureStart(_device);

            _running = true;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "AudioCaptureThread"
            };
            _thread.Start();
        }

        public static void Stop()
        {
            _running = false;
            _thread?.Join(500);
            ALC.CaptureStop(_device);
            ALC.CaptureCloseDevice(_device);
        }

        private static void CaptureLoop()
        {
            // Stereo16: each frame is 2 interleaved 16-bit samples (L, R).
            short[] buffer = new short[BufferSize * 2];
            var fftL = new System.Numerics.Complex[BufferSize];
            var fftR = new System.Numerics.Complex[BufferSize];

            while (_running)
            {
                int available = ALC.GetInteger(_device, AlcGetInteger.CaptureSamples);

                if (available >= BufferSize)
                {
                    ALC.CaptureSamples(_device, buffer, BufferSize);

                    double sumL = 0;
                    double sumR = 0;

                    for (int i = 0; i < BufferSize; i++)
                    {
                        double l = buffer[i * 2]     / 32768.0;
                        double r = buffer[i * 2 + 1] / 32768.0;

                        sumL += l * l;
                        sumR += r * r;

                        fftL[i] = new System.Numerics.Complex(l, 0);
                        fftR[i] = new System.Numerics.Complex(r, 0);
                    }

                    Guitar1.Level = (float)Math.Sqrt(sumL / BufferSize);
                    Guitar2.Level = (float)Math.Sqrt(sumR / BufferSize);

                    // Latch the first time real signal arrives. Deliberately below any
                    // musically useful gate (this answers "is this stream alive at all",
                    // not "is someone playing"), and never reset while capture is open.
                    if (!HasSeenSignal && (Guitar1.Level > 0.0015f || Guitar2.Level > 0.0015f))
                    {
                        HasSeenSignal = true;
                        Console.WriteLine($"[AudioEngine] First signal detected on \"{OpenedDeviceName}\" (L={Guitar1.Level:F4}, R={Guitar2.Level:F4}).");
                    }

                    Fourier.Forward(fftL, FourierOptions.Matlab);
                    Fourier.Forward(fftR, FourierOptions.Matlab);

                    float hzPerBin = (float)SampleRate / BufferSize;
                    int bassStart = (int)(20f / hzPerBin);
                    int bassEnd   = (int)(250f / hzPerBin);
                    int midEnd    = (int)(4000f / hzPerBin);
                    int trebleEnd = (int)(20000f / hzPerBin);

                    Guitar1.Bass   = GetBandEnergy(fftL, bassStart, bassEnd);
                    Guitar1.Mid    = GetBandEnergy(fftL, bassEnd, midEnd);
                    Guitar1.Treble = GetBandEnergy(fftL, midEnd, trebleEnd);

                    Guitar2.Bass   = GetBandEnergy(fftR, bassStart, bassEnd);
                    Guitar2.Mid    = GetBandEnergy(fftR, bassEnd, midEnd);
                    Guitar2.Treble = GetBandEnergy(fftR, midEnd, trebleEnd);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }

        private static float GetBandEnergy(System.Numerics.Complex[] fft, int startBin, int endBin)
        {
            double sum = 0;
            int count = endBin - startBin;
            if (count <= 0) return 0f;

            for (int i = startBin; i < endBin && i < fft.Length / 2; i++)
                sum += fft[i].Magnitude;

            return (float)(sum / count);
        }
    }
}
