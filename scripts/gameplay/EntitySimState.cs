using Godot;

// Persistent simulation state for an entity. Lives in WorldState across save/load
// and outlives the spawned Node3D — when a chunk unloads, the node is freed but
// this state stays so the entity can be re-materialized later with its current
// values intact. Mover-type entities (Mob, Loot) are expected to write their
// current position back into WorldPosition before being freed.
public abstract class EntitySimState
{
    // Mutable so movers can sync their current position back before unload.
    // Non-movers leave it equal to the spawn position.
    public Vector3 WorldPosition;
    public readonly PackedScene Scene;

    protected EntitySimState(Vector3 worldPosition, PackedScene scene)
    {
        WorldPosition = worldPosition;
        Scene = scene;
    }

    // Returns null if this sim state should not materialize an entity right now
    // (e.g. picked up loot, dead mob).
    public abstract Node3D CreateEntity(World world);
}
