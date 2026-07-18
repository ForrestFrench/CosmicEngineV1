using System;

namespace CosmicEngine.App.Rendering.Video
{
    /// <summary>
    /// Phase 1 Hybrid Proof (video-atoms MILESTONE_BREAKDOWN.md, VIDEO_SYSTEM_ARCHITECTURE.md §2.2a).
    /// The mandatory decode seam: everything else in the engine (VideoTexture, scenes, any future
    /// compositor/director) references only this interface, never a concrete decoder or ffmpeg
    /// directly. Swapping in a hardware decoder (VideoToolbox/QuickSync), LibVLC, or an image
    /// sequence later means writing a new implementation class and changing one construction site -
    /// zero changes anywhere else.
    /// </summary>
    public interface IVideoDecoder : IDisposable
    {
        /// <summary>Open (and begin decoding) the given video file. Throws a loud, actionable
        /// exception if the file or the underlying decode transport is unavailable.</summary>
        /// <summary>
        /// Visual Composer Sandbox addition: optional trim window. startSec/durationSec of 0 means
        /// "no trim" (full-file looping decode, unchanged Phase 1 behavior). When durationSec > 0,
        /// implementations should loop only the [startSec, startSec+durationSec) window rather than
        /// the whole file - lets a scene pick a specific hero window out of a long source clip
        /// without pre-cutting a new file on disk.
        /// </summary>
        void Open(string path, float startSec = 0f, float durationSec = 0f);

        int Width { get; }
        int Height { get; }
        float Fps { get; }
        float DurationSec { get; }

        /// <summary>
        /// Phase 3 Effect Stack v1 (MILESTONE_BREAKDOWN.md Phase 3, VIDEO_SYSTEM_ARCHITECTURE.md
        /// §2.2 "Playback speed = frame-release pacing on the reader thread"). Multiplier applied to
        /// decode-thread frame-release pacing, clamped by the implementation to a sane range
        /// (FfmpegPipeDecoder: 0.25x-2x). 1.0 = unchanged Phase 1 real-time playback. Settable live,
        /// read every reader-thread iteration - no restart required. Implementations that cannot
        /// support variable pacing may treat this as a no-op default of 1.0.
        /// </summary>
        float PlaybackSpeed { get; set; }

        /// <summary>
        /// Non-blocking. If a new, complete BGRA frame is ready since the last call, copies it into
        /// <paramref name="destination"/> (length must be >= Width*Height*4) and returns true.
        /// Returns false (destination left untouched) if no new frame is ready yet - callers should
        /// hold and re-use whatever they already have (hold-last-frame on stall), never block.
        /// </summary>
        bool TryAcquireFrame(byte[] destination);
    }
}
