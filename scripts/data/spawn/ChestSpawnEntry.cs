using System;
using Godot;

[GlobalClass]
public partial class ChestSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    // Optional alternate scene chosen 50% of the time when set (e.g. a
    // poison chest variant). Null = always use Scene.
    [Export] public PackedScene altScene;
    // Contents the chest drops on open, with per-item min/max for variance.
    // Ranges are rolled here at worldgen, then baked into the ChestSimState
    // as concrete ItemCounts — opening the chest just ejects the resolved
    // counts, so a chest that rolled "4 mushrooms" at gen time always
    // drops 4 (no re-roll on open, no surprise between save/load).
    [Export] public ItemCountRange[] lootItems = [];

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        PackedScene chestScene = altScene != null && rng.NextDouble() < 0.5
            ? altScene
            : scene;
        var chest = new ChestSimState(position, chestScene)
        {
            // This chest's own authored loot, plus any zone-unique drops for the
            // zone it spawned in (ZoneGenData.zoneLoot, threaded via SpawnContext)
            // — so a region's signature loot rides every chest without forking the
            // shared chest / spawn-group resources.
            LootItems = Combine(Resolve(lootItems, rng), Resolve(context?.ZonePerChestLoot, rng)),
            SpawnConditions = context?.SpawnConditions ?? ESpawnConditions.None,
        };
        ws.AddEntity(chest);
    }

    // Concatenate two already-rolled loot arrays; either may be null/empty.
    private static ItemCount[] Combine(ItemCount[] a, ItemCount[] b)
    {
        if (a == null || a.Length == 0) { return b; }
        if (b == null || b.Length == 0) { return a; }
        var merged = new ItemCount[a.Length + b.Length];
        a.CopyTo(merged, 0);
        b.CopyTo(merged, a.Length);
        return merged;
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
