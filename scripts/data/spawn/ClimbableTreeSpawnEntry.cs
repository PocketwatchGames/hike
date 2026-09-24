using System;
using Godot;

// Scatters climbable trees across a zone's surface. Climbing one lifts the
// player into the bird's-eye overlook and conceals them from mobs (see
// ClimbableTree / Player.EnterClimbableTree). Stateless like the tree itself —
// no per-instance authoring beyond the scene and the area rate.
[GlobalClass]
public partial class ClimbableTreeSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new ClimbableTreeSimState(position, scene)
        {
            RotationY = FacingY(context),
        });
    }
}
