using Godot;

// A plain item and how many: one row of what an item salvages into
// (ItemData.salvage) or costs to repair (GearData.repair). Certain, not rolled,
// so either can be planned; and no ItemDescriptor, because a material carries
// no mods.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class MaterialCount : Resource
{
	[Export] public ItemData item;
	[Export] public int count = 1;
}
