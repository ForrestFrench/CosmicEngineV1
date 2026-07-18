using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace CosmicEngine.App.Rendering.Video
{
    /// <summary>
    /// Phase 1 Hybrid Proof - IVideoDecoder implementation #1 (VIDEO_SYSTEM_ARCHITECTURE.md §2.2).
    /// Spawns a system `ffmpeg` subprocess that decodes the given file to a raw BGRA stream on
    /// stdout, read by a dedicated background thread into a 3-frame ring buffer - mirrors
    /// Audio/AudioEngine.cs's CaptureLoop background-thread + volatile-handoff idiom. The render
    /// thread never blocks: TryAcquireFrame just checks whether a new frame has landed since the
    /// last call and hands back a copy if so (hold-last-frame on stall/no-new-frame).
    ///
    /// This is the ONLY class in the engine that knows ffmpeg exists - everything else talks to
    /// IVideoDecoder.
    /// </summary>
    public class FfmpegPipeDecoder : IVideoDecoder
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public float Fps { get; private set; }
        public float DurationSec { get; private set; }

        private volatile float _playbackSpeed = 1.0f;

        /// <summary>
        /// Phase 3 Effect Stack v1: playback speed as decode-rate pacing on the reader thread (see
        /// VIDEO_SYSTEM_ARCHITECTURE.md §2.2), not a shader effect. Clamped to 0.25x-2x per spec.
        /// Settable live (volatile field, read once per ReaderLoop iteration) - no process restart.
        /// </summary>
        public float PlaybackSpeed
        {
            get => _playbackSpeed;
            set => _playbackSpeed = Math.Clamp(value, 0.25f, 2.0f);
        }

        private Process? _process;
        private Thread? _readerThread;
        private volatile bool _running;
        private volatile bool _disposed;

        // 3-frame ring buffer of raw BGRA bytes, per VIDEO_SYSTEM_ARCHITECTURE.md §2.2.
        private byte[]?[] _ring = new byte[3][];
        private int _writeIndex;   // next slot the reader thread will fill
        private volatile int _latestIndex = -1; // slot index of the newest complete frame, -1 = none yet
        private readonly object _ringLock = new object();

        private int _frameSize; // Width * Height * 4

        public void Open(string path, float startSec = 0f, float durationSec = 0f)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"FfmpegPipeDecoder: video file not found: {path}");

            (Width, Height, Fps, DurationSec) = Probe(path);
            if (Width <= 0 || Height <= 0)
            {
                // Probe couldn't read stream geometry from ffmpeg's own stderr banner - rather than
                // guess, fail loudly (this would otherwise silently corrupt every downstream frame
                // read, since frame size is derived from Width*Height*4).
                throw new Exception(
                    $"FfmpegPipeDecoder: could not determine video dimensions for '{path}' from ffmpeg's " +
                    "probe output. The file may be corrupt or an unsupported container/codec.");
            }

            _frameSize = Width * Height * 4;
            for (int i = 0; i < _ring.Length; i++) _ring[i] = new byte[_frameSize];

            string ffmpegPath = ResolveFfmpegOrThrow();

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                // -stream_loop -1: continuous looping decode, so Phase 1's "hardcoded clip" plays
                // indefinitely without the engine having to restart the process itself (deferred
                // per VIDEO_SYSTEM_ARCHITECTURE.md §2.2 "Loop" - hold-last-frame + process-restart
                // loop is judged sufficient for M2/M3; real crossfade looping is a later milestone).
                // Phase 3 Effect Stack v1: dropped "-re" (which paces ffmpeg's own output at a fixed
                // 1x) so ffmpeg decodes as fast as it can into the ring buffer; real-time pacing plus
                // the live-adjustable 0.25x-2x PlaybackSpeed multiplier is now applied entirely on
                // our own reader thread below (VIDEO_SYSTEM_ARCHITECTURE.md §2.2 "Playback speed =
                // frame-release pacing on the reader thread").
                // Visual Composer Sandbox trim support: when durationSec > 0, seek to startSec and
                // limit each loop iteration to durationSec via -ss/-t placed BEFORE -i (fast input
                // seek) - "-stream_loop -1" then re-applies the same trimmed window on every loop,
                // so the decoder plays only the requested hero window rather than the whole file.
                Arguments = durationSec > 0f
                    ? $"-stream_loop -1 -ss {startSec.ToString(System.Globalization.CultureInfo.InvariantCulture)} -t {durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{path}\" -f rawvideo -pix_fmt bgra -an pipe:1"
                    : $"-stream_loop -1 -i \"{path}\" -f rawvideo -pix_fmt bgra -an pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                _process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"FfmpegPipeDecoder: failed to launch ffmpeg at '{ffmpegPath}': {ex.Message}");
            }

            if (_process == null)
                throw new Exception("FfmpegPipeDecoder: Process.Start returned null unexpectedly.");

            // Drain stderr on its own thread so ffmpeg never blocks on a full stderr pipe buffer.
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginErrorReadLine();

            _running = true;
            _readerThread = new Thread(ReaderLoop)
            {
                IsBackground = true,
                Name = "VideoDecodeReaderThread"
            };
            _readerThread.Start();

            Console.WriteLine($"[FfmpegPipeDecoder] Opened '{path}' ({Width}x{Height} @ {Fps:F2}fps, {DurationSec:F1}s, looping).");
        }

        private void ReaderLoop()
        {
            if (_process == null) return;
            var stdout = _process.StandardOutput.BaseStream;
            byte[] readBuf = new byte[_frameSize];

            // Phase 3 Effect Stack v1: self-paced frame release. ffmpeg (without "-re") decodes
            // into this thread as fast as it can; we hold each decoded frame for
            // (1/Fps)/PlaybackSpeed before publishing it into the ring buffer, so the *rate* new
            // frames become visible to TryAcquireFrame matches the requested speed - not the raw
            // decode rate. sourceFps falls back to 30 if probing ever returned 0.
            float sourceFps = Fps > 0f ? Fps : 30f;
            var frameStopwatch = System.Diagnostics.Stopwatch.StartNew();
            double nextReleaseMs = 0;

            try
            {
                while (_running)
                {
                    int totalRead = 0;
                    while (totalRead < _frameSize)
                    {
                        int n = stdout.Read(readBuf, totalRead, _frameSize - totalRead);
                        if (n <= 0)
                        {
                            // Pipe closed (process exited/stalled) - stop the reader; TryAcquireFrame
                            // will keep returning false and the caller holds its last frame.
                            _running = false;
                            break;
                        }
                        totalRead += n;
                        if (!_running) break;
                    }

                    if (!_running || totalRead < _frameSize) break;

                    // Pace release: sleep until this frame's scheduled release time before
                    // publishing it, so downstream frame *availability* (not raw decode speed)
                    // reflects PlaybackSpeed. A stalled/late decode never sleeps negative.
                    double frameIntervalMs = 1000.0 / sourceFps / Math.Clamp(_playbackSpeed, 0.25f, 2.0f);
                    nextReleaseMs += frameIntervalMs;
                    double waitMs = nextReleaseMs - frameStopwatch.Elapsed.TotalMilliseconds;
                    if (waitMs > 0 && waitMs < 2000 && _running)
                        Thread.Sleep((int)waitMs);
                    else if (waitMs < 0)
                        nextReleaseMs = frameStopwatch.Elapsed.TotalMilliseconds; // resync if we fell behind

                    lock (_ringLock)
                    {
                        var slot = _ring[_writeIndex];
                        Buffer.BlockCopy(readBuf, 0, slot!, 0, _frameSize);
                        _latestIndex = _writeIndex;
                        _writeIndex = (_writeIndex + 1) % _ring.Length;
                    }
                }
            }
            catch (Exception ex)
            {
                // Non-fatal per VIDEO_SYSTEM_ARCHITECTURE.md §2.2 "Failure modes: all non-fatal, all
                // logged" - decode stall/pipe error just stops new frames; caller holds last frame.
                Console.WriteLine($"[FfmpegPipeDecoder] Reader thread stopped: {ex.Message}");
            }
        }

        private int _lastAcquiredIndex = -1;

        public bool TryAcquireFrame(byte[] destination)
        {
            int idx = _latestIndex;
            if (idx < 0 || idx == _lastAcquiredIndex) return false;
            if (destination.Length < _frameSize) return false;

            lock (_ringLock)
            {
                var slot = _ring[idx];
                if (slot == null) return false;
                Buffer.BlockCopy(slot, 0, destination, 0, _frameSize);
            }
            _lastAcquiredIndex = idx;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;

            // Guaranteed child-process termination on every exit path (Dispose, world switch,
            // engine quit) - see MILESTONE_BREAKDOWN.md Phase 1 acceptance item. Kill -9 of the
            // whole CosmicEngine process is a documented, unmitigated gap (KNOWN_LIMITATIONS.md) -
            // there is no managed-code hook that survives SIGKILL.
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FfmpegPipeDecoder] Dispose: error killing ffmpeg process (non-fatal): {ex.Message}");
            }

            _readerThread?.Join(500);
            _process?.Dispose();
        }

        /// <summary>
        /// Locates a usable ffmpeg binary (PATH, then common Homebrew locations). Throws one loud,
        /// actionable error with an install hint if none is found - per Phase 1's "loud actionable
        /// error if ffmpeg is missing" requirement, matching the house pattern set by the
        /// ControlServer HttpListener retry fix (AUDIT.md).
        /// </summary>
        private static string ResolveFfmpegOrThrow()
        {
            string[] candidates =
            {
                "ffmpeg", // resolved via PATH by ProcessStartInfo/exec search
                "/opt/homebrew/bin/ffmpeg", // Apple Silicon Homebrew
                "/usr/local/bin/ffmpeg",    // Intel Homebrew
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (candidate == "ffmpeg" || File.Exists(candidate))
                    {
                        // For the bare "ffmpeg" PATH case we can't cheaply confirm existence without
                        // spawning it; ProcessStartInfo.Start will throw Win32Exception if it can't
                        // be found, which the Open() caller wraps into the actionable message below.
                        // For absolute paths, File.Exists already confirmed it's really there.
                        return candidate;
                    }
                }
                catch { /* keep trying candidates */ }
            }

            throw new Exception(
                "FfmpegPipeDecoder: no usable 'ffmpeg' binary found on PATH or in the common Homebrew " +
                "install locations (/opt/homebrew/bin, /usr/local/bin). Cosmic Engine's hybrid " +
                "video worlds require the system ffmpeg binary (not bundled). Install it with " +
                "Homebrew: `brew install ffmpeg` (macOS). After installing, confirm with `ffmpeg -version` " +
                "in a fresh terminal before relaunching Cosmic Engine.");
        }

        /// <summary>
        /// Runs `ffmpeg -i <path>` (no output file - always exits nonzero, which is expected) and
        /// parses width/height/fps/duration out of its stderr banner. Avoids requiring a separate
        /// ffprobe dependency; ffmpeg alone is sufficient for Phase 1.
        /// </summary>
        private static (int w, int h, float fps, float dur) Probe(string path)
        {
            string ffmpegPath = ResolveFfmpegOrThrow();
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{path}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            string stderrText;
            using (var p = Process.Start(psi))
            {
                if (p == null)
                    throw new Exception("FfmpegPipeDecoder: probe failed to launch ffmpeg.");
                stderrText = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
            }

            int w = 0, h = 0;
            float fps = 30f, dur = 0f;

            var streamMatch = Regex.Match(stderrText, @"Video:.*?(\d{2,5})x(\d{2,5})");
            if (streamMatch.Success)
            {
                w = int.Parse(streamMatch.Groups[1].Value);
                h = int.Parse(streamMatch.Groups[2].Value);
            }

            var fpsMatch = Regex.Match(stderrText, @"([\d.]+)\s+fps");
            if (fpsMatch.Success) fps = float.Parse(fpsMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            var durMatch = Regex.Match(stderrText, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
            if (durMatch.Success)
            {
                int hh = int.Parse(durMatch.Groups[1].Value);
                int mm = int.Parse(durMatch.Groups[2].Value);
                float ss = float.Parse(durMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                dur = hh * 3600 + mm * 60 + ss;
            }

            return (w, h, fps, dur);
        }
    }
}
