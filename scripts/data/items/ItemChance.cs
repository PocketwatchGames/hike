using Godot;

// An ItemCount that is present only some of the time: rolled once, on its own,
// against `chance`. A subclass rather than a field on ItemCount because most
// item lists (inventories, species loot, chest contents) are certain, and a
// chance there would be a field that does nothing.
[GlobalClass]
public partial class ItemChance : ItemCount
{
	[Export(PropertyHint.Range, "0,1,0.01")] public float chance = 1f;
}
