using System;
using Godot;

// A door. The facing is the hinge line, so this is one of the entries an
// authored placement must be aimed at — a door dropped square-on into a wall
// running the other way opens into rock.
//
// Single and double leaf are the same entry with a different scene, the way the
// two fountains are: which one it is, is authored in the .tscn (leaf count,
// collider, the voxels it stamps), so a picker here would be a control that has
// to agree with a scene it cannot see.
[GlobalClass]
public partial class DoorSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new DoorSimState(position, FacingY(context), scene));
    }
}
