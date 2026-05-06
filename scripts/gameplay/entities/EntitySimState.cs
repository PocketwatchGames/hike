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

    // True if a node should currently exist for this state. Default mirrors
    // CreateEntity's "always materialize" contract; overrides drive
    // SpawnAtNight-style gating. World re-evaluates this on day/night
    // transitions to spawn or despawn nodes for already-active chunks, so
    // night-only entities don't need a chunk reload to appear.
    public virtual bool ShouldSpawn(World world) => true;

    // Live node reference, set by World when the entity is materialized and
    // cleared via TreeExiting when it leaves the scene. Used by the
    // day/night refresh pass to find which states currently have a node.
    // Not serialized — purely runtime bookkeeping.
    public Node3D RuntimeNode;

    // The voxel cell this entity occupies for the purposes of mob pathfinding,
    // or null if it doesn't block walkability. World registers/unregisters this
    // cell as the entity spawns/despawns; the walkability sampler treats any
    // surface column whose stand-in cells are blocked as unwalkable. Only
    // entities with a meaningful physical footprint (trees, chests) should
    // override — pickups, decorative grass, and torches return null.
    public virtual Vector3I? PathBlockerCell => null;
}
