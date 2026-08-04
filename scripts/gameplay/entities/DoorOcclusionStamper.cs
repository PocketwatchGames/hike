using System.Collections.Generic;

// Reconciles every door's doorway voxels with its authored open/closed state,
// before the world's sunlight is computed.
//
// A door blocks light and navigation with a Barrier voxel written into its
// doorway, and nothing else in the pipeline writes one — the editor's Door
// brush only carves the opening. Without this pass a closed door occludes
// nothing until the player happens to shut it once, so the baked sun field (and
// every vertex bake sitting on top of it) disagrees with what the door visibly
// is: sun pours through a shut door, and torchlight with it.
//
// Runs alongside FoliageStamper and before LightEngine.Relight, for the same
// reason: the sun pass has to see the occluder to bake it. Separate from that
// walk because this writes VOXELS rather than the non-voxel occlusion fields
// FoliageStamper clears, so it has no ordering relationship with it.
public static class DoorOcclusionStamper
{
    public static void Stamp(WorldState world)
    {
        if (world == null)
        {
            return;
        }
        int doors = 0;
        foreach (List<EntitySimState> bucket in world._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i] is not DoorSimState door)
                {
                    continue;
                }
                // Null `changed` — nothing to relight incrementally, the full
                // sun pass runs straight after this.
                Door.ApplyOcclusion(world, door, null);
                doors++;
            }
        }
        if (doors > 0)
        {
            Godot.GD.Print($"[DoorOcclusionStamper] doors={doors}");
        }
    }
}
