namespace CosmicEngine.App.Engine
{
    public static class Time
    {
        public static float Total = 0f;

        public static void Update(float deltaTime)
        {
            Total += deltaTime;
        }
    }
}