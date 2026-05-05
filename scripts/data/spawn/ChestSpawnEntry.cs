using System;
using Godot;

[GlobalClass]
public partial class ChestSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;
    [Export] public PackedScene LootScene;
    [Export] public int LootCountMin = 3;
    [Export] public int LootCountMax = 6;

    public override void Spawn(WorldState ws, Vector3 position, Random rng)
    {
        if (Scene == null || LootScene == null)
        {
            return;
        }
        int lootCount = rng.Next(LootCountMin, LootCountMax + 1);
        ws.AddEntity(new ChestSimState(position, Scene, lootCount, LootScene));
    }
}
