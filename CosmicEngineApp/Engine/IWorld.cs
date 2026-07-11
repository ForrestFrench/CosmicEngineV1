using CosmicEngine.App.Audio;

namespace CosmicEngine.App.Engine
{
    /// <summary>
    /// Contract that every Cosmic Engine world must fulfill.
    /// The engine calls these methods in order each frame.
    /// Adding a new world means implementing this interface — nothing else changes.
    /// </summary>
    public interface IWorld
    {
        /// <summary>Called once when the world is made active. Load shaders and GPU resources here.</summary>
        void Load();

        /// <summary>Called every frame before Render. Update world state, react to audio here.</summary>
        void Update(float deltaTime, AudioSignal audio);

        /// <summary>Called every frame. Issue draw calls here.</summary>
        void Render();

        /// <summary>Called when the world is unloaded or the engine shuts down. Free GPU resources here.</summary>
        void Unload();
    }
}
