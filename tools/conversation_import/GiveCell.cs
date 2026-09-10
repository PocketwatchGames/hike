using System;

// An item gift parsed out of a sheet's action cell: `give:<item> [count]`,
// spelled exactly the way the console's `give` verb is, and resolving the item
// the same way — by the basename of a .tres under resources/data/items/.
//
// The importer emits a DropLootAction of its own for it, so a one-item handover
// needs no authored .tres. That was the whole cost of the old give_lantern /
// give_vyeshal_vocab1 files: three nested resources (DropLootAction ->
// ItemCount -> ItemDescriptor) to say one item's name.
//
// An authored .tres is still the answer when the gift is not anonymous — when it
// carries ItemDescriptor mods or a level (a Fragile bomb), or when several items
// together are one named concept worth reusing across NPCs.
class GiveRef
{
	public ResRef Item;
	public int Count = 1;
}

static class GiveCell
{
	const string Prefix = "give:";

	// True when the cell token is the inline gift form rather than the name of
	// an authored .tres. Note that `give_lantern` is NOT this: the separator is
	// the colon, so an authored action may still be named give_something.
	public static bool IsGiveToken(string token)
	{
		return token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
	}

	public static GiveRef Parse(string token, ResourceIndex index, SheetRow row, Report report)
	{
		string body = token.Substring(Prefix.Length).Trim();
		if (body.Length == 0)
		{
			report.Error(row, $"'{token}' names no item - say 'give:lantern'");
			return null;
		}

		string[] parts = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length > 2)
		{
			report.Error(row, $"'{token}' has more than an item and a count - one gift per token, ';'-separate several");
			return null;
		}
		int count = 1;
		if (parts.Length == 2 && (!int.TryParse(parts[1], out count) || count < 1))
		{
			report.Error(row, $"'{token}' has a count of '{parts[1]}', which is not a whole number of 1 or more");
			return null;
		}

		ResRef item = index.Item(parts[0]);
		if (item == null)
		{
			report.Error(row, $"unknown item '{parts[0]}' - no .tres by that basename under resources/data/items/ (the same names `give ?` lists in the console)");
			return null;
		}
		return new GiveRef { Item = item, Count = count };
	}
}
