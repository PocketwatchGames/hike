using System;
using Godot;

[GlobalClass]
public partial class ChestSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;
    [Export] public PackedScene LootScene;
    [Export] public int LootCountMin = 3;
    [Export] public int LootCountMax = 6;

    // When true, the spawned ChestSimState is marked SpawnAtNight so its node
    // only appears when its chunk activates after dark.
    [Export] public bool SpawnAtNight;

    public override void Spawn(WorldState ws, Vector3 position, Random rng)
    {
        if (Scene == null || LootScene == null)
        {
            return;
        }
        int lootCount = rng.Next(LootCountMin, LootCountMax + 1);
        var chest = new ChestSimState(position, Scene, lootCount, LootScene);
        chest.SpawnAtNight = SpawnAtNight;
        ws.AddEntity(chest);
    }
}
