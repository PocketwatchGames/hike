using System;

// An item gift parsed out of a sheet's action cell: `spawn_loot:<item> [count]`,
// spelled exactly the way the console's `spawn_loot` verb is, and resolving the item
// the same way — by the basename of a .tres under resources/data/items/, or
// under this world's own worlds/<world>/items/.
//
// The importer emits a DropLootAction of its own for it, so a one-item handover
// needs no authored .tres. That was the whole cost of the old give_lantern /
// give_vyeshal_vocab1 files: three nested resources (DropLootAction ->
// ItemCount -> ItemDescriptor) to say one item's name.
//
// An authored .tres is still the answer when the gift is not anonymous — when it
// carries ItemDescriptor mods or a level (a Fragile bomb), or when several items
// together are one named concept worth reusing across NPCs.
class SpawnLootRef
{
	public ResRef Item;
	public int Count = 1;
}

static class SpawnLootCell
{
	// Resolves a parsed `spawn_loot:` (ActionCommand) against this world's items.
	public static SpawnLootRef Resolve(ActionCommand command, ResourceIndex index, string world, SheetRow row, Report report)
	{
		ResRef item = index.Item(world, command.Name);
		if (item == null)
		{
			report.Error(row, $"unknown item '{command.Name}' - no .tres by that basename under resources/data/items/ or worlds/{world}/items/ (the same names `spawn_loot ?` lists in the console)");
			return null;
		}
		return new SpawnLootRef { Item = item, Count = command.Count };
	}
}
