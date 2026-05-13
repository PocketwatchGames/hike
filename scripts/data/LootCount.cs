using Godot;

// Authored (item, count) pair. Used by MobData.loot to declare what a mob
// drops on death — each entry ejects `count` instances of `item` like a
// chest spawns its piles. ItemData rather than LootData so non-loot items
// (a unique key, a dropped weapon) can be authored here too.
[GlobalClass]
public partial class LootCount : Resource
{
    [Export] public ItemData item;
    [Export] public int count = 1;
}
