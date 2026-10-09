using System;
using Godot;

// A torch or lamp, lit until the player douses it. Use CampfireSpawnEntry for
// the campfire, which is its own entity.
[GlobalClass]
public partial class TorchSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new TorchSimState(position, scene)
        {
            RotationY = FacingY(context),
        });
    }
}
