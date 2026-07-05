using OpenTK.Mathematics;

namespace CosmicEngine.App.Engine
{
    public class Camera
    {
        public float   Zoom   { get; private set; } = 1.0f;
        public Vector2 Offset { get; private set; } = Vector2.Zero;

        private float _time;

        private static readonly float[] FreqX = { 0.00631f, 0.01373f, 0.02711f };
        private static readonly float[] AmpX  = { 0.038f,   0.016f,   0.007f   };
        private static readonly float[] FreqY = { 0.00714f, 0.01193f, 0.02417f };
        private static readonly float[] AmpY  = { 0.030f,   0.013f,   0.006f   };

        public void Update(float deltaTime, float bass1 = 0f)
        {
            _time += deltaTime;
            float dx = 0f, dy = 0f;
            for (int i = 0; i < 3; i++)
            {
                dx += MathF.Sin(_time * FreqX[i]) * AmpX[i];
                dy += MathF.Cos(_time * FreqY[i]) * AmpY[i];
            }
            Offset = new Vector2(dx, dy);
            Zoom = 1.0f
                + 0.16f * MathF.Sin(_time * 0.0112f)
                + 0.05f * MathF.Sin(_time * 0.0231f + 1.4f);
        }

        public Vector2 Transform(Vector2 p) => (p - Offset) / Zoom;
    }
}
