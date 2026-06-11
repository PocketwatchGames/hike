using Godot;

// A stack of a (possibly modded) item — an ItemDescriptor paired with a count.
// Composition, not inheritance: an ItemCount HAS-A complex item, it isn't a kind
// of one. Used by player-spawn inventories, mob / chest loot tables, and
// worldgen stashes. Read `descriptor.item` for the ItemData and
// `descriptor.CreateState()` to build a runtime state with the mods composed on.
[GlobalClass]
public partial class ItemCount : Resource
{
	[Export] public ItemDescriptor descriptor;
	[Export] public int count = 1;
}
