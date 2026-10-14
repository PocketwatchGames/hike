using System;
using Godot;

// Places a bed. No per-instance tuning — the heal rate lives on the Bed scene.
[GlobalClass]
public partial class BedSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new BedSimState(position, scene)
        {
            RotationY = FacingY(context),
        });
    }
}
