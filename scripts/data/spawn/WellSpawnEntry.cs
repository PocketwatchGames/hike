using System;
using Godot;

[GlobalClass]
public partial class WellSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new WellSimState(position, scene));
    }
}
