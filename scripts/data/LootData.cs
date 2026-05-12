using Godot;

// Items with loot-specific behavior — currently empty, but kept around as a
// distinct subclass so future loot-flavored fields (spoilageTime for berries,
// decay-into-nothing timers, drop-on-death triggers) have a home that doesn't
// pollute ItemData. Any plain ItemData can already be dropped in the world
// through the shared Loot scene; LootData exists for items that ALSO need
// world-on-ground dynamics beyond a basic pickup.
[GlobalClass]
public partial class LootData : ItemData
{
}
