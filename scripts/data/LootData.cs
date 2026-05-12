using Godot;

// Items with loot-specific behavior — currently empty, but kept around as a
// distinct subclass so future loot-flavored fields (spoilageTime for berries,
// decay-into-nothing timers, drop-on-death triggers) have a home that doesn't
// pollute ItemData. ItemData already carries Scene + AutoPickup so any plain
// ItemData can be dropped in the world; LootData exists for items that ALSO
// need world-on-ground dynamics beyond a basic pickup.
[GlobalClass]
public partial class LootData : ItemData
{
}
