using System;
using Godot;

// Places a crafting bench. No per-instance state.
[GlobalClass]
public partial class CraftingBenchSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new CraftingBenchSimState(position, scene)
        {
            RotationY = FacingY(context),
        });
    }
}
