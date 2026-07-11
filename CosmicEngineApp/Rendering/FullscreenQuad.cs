using OpenTK.Graphics.OpenGL4;
using System;

namespace CosmicEngine.App.Rendering
{
    /// <summary>
    /// A reusable fullscreen triangle pair covering the entire viewport.
    /// Every world that uses a fullscreen fragment shader uses this — no duplication.
    /// Call Bind() before drawing and Draw() to issue the draw call.
    /// </summary>
    public class FullscreenQuad : IDisposable
    {
        private int _vao;
        private int _vbo;
        private bool _disposed;

        private static readonly float[] Vertices =
        {
            -1f, -1f,
             1f, -1f,
             1f,  1f,
            -1f, -1f,
             1f,  1f,
            -1f,  1f,
        };

        public FullscreenQuad()
        {
            _vao = GL.GenVertexArray();
            GL.BindVertexArray(_vao);

            _vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer,
                Vertices.Length * sizeof(float),
                Vertices,
                BufferUsageHint.StaticDraw);

            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);

            GL.BindVertexArray(0);
        }

        public void Draw()
        {
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
            GL.BindVertexArray(0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            GL.DeleteBuffer(_vbo);
            GL.DeleteVertexArray(_vao);
            _disposed = true;
        }
    }
}
