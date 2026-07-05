using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace CosmicEngine.App.Rendering
{
    public class ShaderProgram : IDisposable
    {
        private readonly int _handle;
        private bool _disposed;

        public ShaderProgram(string vertexPath, string fragmentPath)
        {
            int vert = Compile(vertexPath,   ShaderType.VertexShader);
            int frag = Compile(fragmentPath, ShaderType.FragmentShader);
            _handle  = GL.CreateProgram();
            GL.AttachShader(_handle, vert);
            GL.AttachShader(_handle, frag);
            GL.LinkProgram(_handle);
            GL.GetProgram(_handle, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0)
                throw new Exception($"Link error:\n{GL.GetProgramInfoLog(_handle)}");
            GL.DetachShader(_handle, vert); GL.DeleteShader(vert);
            GL.DetachShader(_handle, frag); GL.DeleteShader(frag);
        }

        public void Use() => GL.UseProgram(_handle);

        public void SetFloat(string name, float value)
        {
            int loc = GL.GetUniformLocation(_handle, name);
            if (loc >= 0) GL.Uniform1(loc, value);
        }

        public void SetInt(string name, int value)
        {
            int loc = GL.GetUniformLocation(_handle, name);
            if (loc >= 0) GL.Uniform1(loc, value);
        }

        public void SetVector3(string name, float x, float y, float z)
        {
            int loc = GL.GetUniformLocation(_handle, name);
            if (loc >= 0) GL.Uniform3(loc, x, y, z);
        }

        public void SetVector3(string name, Vector3 v) =>
            SetVector3(name, v.X, v.Y, v.Z);

        private static int Compile(string path, ShaderType type)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Shader not found: {path}");
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, File.ReadAllText(path));
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
                throw new Exception($"Compile error {path}:\n{GL.GetShaderInfoLog(s)}");
            return s;
        }

        public void Dispose()
        {
            if (_disposed) return;
            GL.DeleteProgram(_handle);
            _disposed = true;
        }
    }
}
