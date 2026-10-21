using Godot;
using System.Collections.Generic;

// The body of a small animal, picked up off its corpse (MobData.carcass) and
// carried as an item. What it is cut into lives here and only here: salvaging it
// (salvageTool = Blade) and butchering the corpse where it lies both roll
// `harvest`. Author it with maxStack 1 — each carcass is a whole animal.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class CarcassData : ItemData
{
	[Export] public ItemCountRange[] harvest = System.Array.Empty<ItemCountRange>();

	public override bool IsSalvageable => harvest != null && harvest.Length > 0;

	protected override void RollSalvageYields(List<ItemState> into)
	{
		ItemCountRange.ResolveAll(harvest, System.Random.Shared, into);
	}
}
