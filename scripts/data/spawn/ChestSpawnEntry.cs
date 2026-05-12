using System;
using Godot;

[GlobalClass]
public partial class ChestSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;
    // Optional alternate scene chosen 50% of the time when set (e.g. a
    // poison chest variant). Null = always use Scene.
    [Export] public PackedScene AltScene;
    [Export] public int LootCountMin = 3;
    [Export] public int LootCountMax = 6;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        int lootCount = rng.Next(LootCountMin, LootCountMax + 1);
        PackedScene chestScene = AltScene != null && rng.NextDouble() < 0.5
            ? AltScene
            : Scene;
        var chest = new ChestSimState(position, chestScene, lootCount);
        chest.SpawnAtNight = SpawnAtNight;
        ws.AddEntity(chest);
    }
}
