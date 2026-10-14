using System;
using Godot;

// Places an access point to the party stash. No per-instance state — every
// stash opens the one SimState.PartyStash.
[GlobalClass]
public partial class StashSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new StashSimState(position, scene)
        {
            RotationY = FacingY(context),
        });
    }
}
