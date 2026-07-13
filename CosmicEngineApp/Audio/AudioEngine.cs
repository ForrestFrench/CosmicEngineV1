using System;
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

        public static void Start()
        {
            _device = ALC.CaptureOpenDevice(null, SampleRate, ALFormat.Stereo16, BufferSize);

            if (_device == ALCaptureDevice.Null)
                throw new Exception("AudioEngine: could not open capture device. Check interface permissions.");

            try
            {
                string opened = ALC.GetString(new ALDevice(_device.Handle), AlcGetString.CaptureDeviceSpecifier);
                Console.WriteLine($"[AudioEngine] Capture opened: device=\"{opened}\", format=Stereo16, sampleRate={SampleRate}, bufferSize={BufferSize} frames.");
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
