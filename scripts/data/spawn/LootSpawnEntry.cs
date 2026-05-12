using System;
using Godot;

[GlobalClass]
public partial class LootSpawnEntry : SpawnEntryData
{
    [Export] public LootData LootData;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (LootData == null || LootData.Scene == null)
        {
            return;
        }
        ws.AddEntity(new LootSimState(position, LootData));
    }
}
