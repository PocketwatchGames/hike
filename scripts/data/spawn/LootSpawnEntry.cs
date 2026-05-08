using System;
using Godot;

[GlobalClass]
public partial class LootSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        ws.AddEntity(new PropSimState(PropType.AutoLoot, position, Scene));
    }
}
