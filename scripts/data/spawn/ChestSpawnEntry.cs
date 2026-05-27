using System;
using Godot;

[GlobalClass]
public partial class ChestSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;
    // Optional alternate scene chosen 50% of the time when set (e.g. a
    // poison chest variant). Null = always use Scene.
    [Export] public PackedScene AltScene;
    // Contents the chest drops on open, with per-item min/max for variance.
    // Ranges are rolled here at worldgen, then baked into the ChestSimState
    // as concrete ItemCounts — opening the chest just ejects the resolved
    // counts, so a chest that rolled "4 mushrooms" at gen time always
    // drops 4 (no re-roll on open, no surprise between save/load).
    [Export] public ItemCountRange[] LootItems = [];

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        PackedScene chestScene = AltScene != null && rng.NextDouble() < 0.5
            ? AltScene
            : Scene;
        var chest = new ChestSimState(position, chestScene)
        {
            LootItems = Resolve(LootItems, rng),
            SpawnConditions = spawnConditions,
        };
        ws.AddEntity(chest);
    }

    // Roll every authored range into a concrete ItemCount. Shared with
    // WorldEditor's placement path so editor-spawned and worldgen-spawned
    // chests resolve their ranges the same way.
    public static ItemCount[] Resolve(ItemCountRange[] ranges, Random rng)
    {
        if (ranges == null || ranges.Length == 0)
        {
            return null;
        }
        var resolved = new ItemCount[ranges.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            resolved[i] = ranges[i]?.Resolve(rng);
        }
        return resolved;
    }
}
