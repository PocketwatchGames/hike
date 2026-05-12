using System;
using Godot;

[GlobalClass]
public partial class LootSpawnEntry : SpawnEntryData
{
    [Export] public ItemData Item;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Item == null || Item.Scene == null)
        {
            return;
        }
        ws.AddEntity(new LootSimState(position, Item));
    }
}
