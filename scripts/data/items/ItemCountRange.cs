using Godot;

// Authored count + random spread for an item drop. Rolled at spawn time
// into a concrete ItemCount — used by ChestSpawnEntry so worldgen variance
// is baked into the spawned ChestSimState, not re-rolled each time the
// chest is opened. Resolved count is `count + rng.Next(0, countRange + 1)`,
// so the defaults (count=1, countRange=0) produce a deterministic 1.
[GlobalClass]
public partial class ItemCountRange : Resource
{
    [Export] public ItemData item;
    [Export] public int count = 1;
    [Export] public int countRange = 0;

    public ItemCount Resolve(System.Random rng)
    {
        int spread = Mathf.Max(0, countRange);
        int rolled = count + rng.Next(0, spread + 1);
        // Chest ranges author no mods (no statusEffects on this resource), so the
        // resolved descriptor wraps just the item. If chests ever need modded
        // loot, add a statusEffects list here and the serializer (which persists
        // chest LootItems) will need to carry it too — see EntitySerializer.
        return new ItemCount { descriptor = new ItemDescriptor { item = item }, count = rolled };
    }
}
