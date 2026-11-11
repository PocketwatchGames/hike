using Godot;

// One entity a scene is built from: a spawn entry at a scene-local position and
// yaw. A `.hikescene` stores these, not the states they spawn, so a stamp
// re-spawns each one through the same TrySpawn every other placement takes —
// a scene's door tracks door.tres, and a stamp can fork one entity's entry
// (SubscenePlacement.entityOverrides) without touching the scene.
[GlobalClass]
public partial class SceneEntityPlacement : AuthoredPlacement
{
    // Stable within its scene and never reused (SubsceneEntities.nextId), so a
    // stamp's override keeps naming the same door however the scene is
    // re-authored around it. 0 means "not yet assigned"; the save assigns one.
    [Export] public int id;

    // Scene-local, the same space as the scene's voxels: the scene's
    // SubsceneState.Anchor maps it into the world.
    [Export] public Vector3 position;

    // Radians about +Y, handed to the spawn as SpawnContext.FacingY.
    [Export] public float rotationY;

    // What the entry's own rolls are seeded with, so every stamp of the scene
    // places what the author saw in the editor — the same goblin, the same yaw
    // for an entry that picks its own.
    [Export] public int seed;

    // This same entity at another position — the scene editor's world-space
    // placement re-based into the scene's own space for a save. Keeps the id
    // and shares the fork: it is the same entity, written somewhere else.
    public SceneEntityPlacement CopyAt(Vector3 at)
    {
        return new SceneEntityPlacement
        {
            id = id,
            source = source,
            custom = custom,
            position = at,
            rotationY = rotationY,
            seed = seed,
        };
    }

    // A copy for another document or another spot: same entry, same roll, no
    // id (the destination assigns its own). The fork is forked again, so two
    // placements never share one mutable copy.
    public SceneEntityPlacement CopyWithoutId()
    {
        return new SceneEntityPlacement
        {
            source = source,
            custom = custom?.Fork(),
            position = position,
            rotationY = rotationY,
            seed = seed,
        };
    }
}
