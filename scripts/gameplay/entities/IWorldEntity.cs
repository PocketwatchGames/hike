public interface IWorldEntity
{
    // Called once after the entity has been instantiated and added to the scene tree.
    // Use this to wire up world-side bookkeeping (light map uniforms, lifecycle events, etc.).
    void OnSpawned(Sim sim);
}
