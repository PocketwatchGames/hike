using System;
using Godot;

// Places a rest tent at the spawn point. No per-instance tuning — the sleep
// duration lives on the Tent scene's [Export] _sleepHours.
[GlobalClass]
public partial class TentSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        ws.AddEntity(new TentSimState(position, Scene));
    }
}
