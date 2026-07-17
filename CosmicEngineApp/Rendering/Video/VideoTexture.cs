using OpenTK.Graphics.OpenGL4;
using System;

namespace CosmicEngine.App.Rendering.Video
{
    /// <summary>
    /// Phase 1 Hybrid Proof - GL texture wrapper around an IVideoDecoder. Owns the CPU-side staging
    /// buffer and the GL texture object; Update() does at most one glTexSubImage2D upload per call
    /// (call it once per render frame), per VIDEO_SYSTEM_ARCHITECTURE.md §2.2's upload budget. If
    /// the decoder has no new frame ready, the existing GL texture is left untouched (hold-last-
    /// frame is therefore "free" - no upload, no CPU copy).
    /// </summary>
    public class VideoTexture : IDisposable
    {
        private readonly IVideoDecoder _decoder;
        private readonly byte[] _staging;
        private int _textureId;
        private bool _disposed;

        public int Width => _decoder.Width;
        public int Height => _decoder.Height;
        public int TextureId => _textureId;

        public VideoTexture(IVideoDecoder decoder)
        {
            _decoder = decoder;
            _staging = new byte[Math.Max(1, decoder.Width * decoder.Height * 4)];

            _textureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _textureId);
            // Allocate storage up front (even before the first real frame) so the texture is always
            // bindable - avoids a black-frame-vs-uninitialized-texture ambiguity on the first draw.
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                decoder.Width, decoder.Height, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        /// <summary>Call once per render frame. Uploads a new frame iff the decoder has one ready.</summary>
        public void Update()
        {
            if (!_decoder.TryAcquireFrame(_staging))
                return; // hold-last-frame: GL texture already has the most recent frame we had.

            GL.BindTexture(TextureTarget.Texture2D, _textureId);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Width, Height,
                PixelFormat.Bgra, PixelType.UnsignedByte, _staging);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public void Bind(TextureUnit unit)
        {
            GL.ActiveTexture(unit);
            GL.BindTexture(TextureTarget.Texture2D, _textureId);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_textureId != 0) GL.DeleteTexture(_textureId);
        }
    }
}
