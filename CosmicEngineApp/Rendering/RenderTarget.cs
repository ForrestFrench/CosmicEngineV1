using OpenTK.Graphics.OpenGL4;
using System;

namespace CosmicEngine.App.Rendering
{
    /// <summary>
    /// A fixed-size off-screen render target (framebuffer object).
    /// Worlds always render to this at a fixed internal resolution.
    /// The result is then scaled to fill the window via BlitToScreen.
    /// This decouples shader cost from window size entirely.
    /// </summary>
    public class RenderTarget : IDisposable
    {
        private int  _fbo;
        private int  _texture;
        private bool _disposed;

        public int Width  { get; }
        public int Height { get; }

        public RenderTarget(int width, int height)
        {
            Width  = width;
            Height = height;

            _fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

            _texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texture);
            GL.TexImage2D(
                TextureTarget.Texture2D, 0,
                PixelInternalFormat.Rgb,
                width, height, 0,
                PixelFormat.Rgb, PixelType.UnsignedByte,
                IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _texture, 0);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new Exception($"RenderTarget FBO not complete: {status}");

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        /// <summary>
        /// Bind this target so all subsequent GL draws go here.
        /// Sets the viewport to the fixed internal resolution.
        /// </summary>
        public void Bind()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.Viewport(0, 0, Width, Height);
            GL.Clear(ClearBufferMask.ColorBufferBit);
        }

        /// <summary>
        /// Scale the fixed-size render to fill the actual screen framebuffer.
        /// Call this after all world rendering is done, before SwapBuffers.
        /// screenW/screenH should be the physical framebuffer size
        /// (use window.FramebufferSize, not ClientSize, to handle Retina).
        /// </summary>
        public void BlitToScreen(int screenW, int screenH)
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.Viewport(0, 0, screenW, screenH);
            GL.BlitFramebuffer(
                0, 0, Width, Height,
                0, 0, screenW, screenH,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            GL.DeleteTexture(_texture);
            GL.DeleteFramebuffer(_fbo);
            _disposed = true;
        }
    }
}
