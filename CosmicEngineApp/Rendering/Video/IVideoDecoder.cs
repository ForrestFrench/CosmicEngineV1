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
        void Open(string path);

        int Width { get; }
        int Height { get; }
        float Fps { get; }
        float DurationSec { get; }

        /// <summary>
        /// Non-blocking. If a new, complete BGRA frame is ready since the last call, copies it into
        /// <paramref name="destination"/> (length must be >= Width*Height*4) and returns true.
        /// Returns false (destination left untouched) if no new frame is ready yet - callers should
        /// hold and re-use whatever they already have (hold-last-frame on stall), never block.
        /// </summary>
        bool TryAcquireFrame(byte[] destination);
    }
}
