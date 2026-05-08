using System;
using Godot;

// Plain torch (defaults to lit). Use CampfireSpawnEntry for the campfire
// variant that auto-lights at night and spawns dark.
[GlobalClass]
public partial class TorchSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        ws.AddEntity(new TorchSimState(position, Scene));
    }
}
