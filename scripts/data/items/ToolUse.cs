using Godot;

// One kind of tool an item serves as (ItemData.toolUses), and the durability one
// use spends when the item wears (a GearData). Carrying the item anywhere —
// equipped, belt, backpack or a sack — is enough (Inventory.FindTool).
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ToolUse : Resource
{
	[Export] public EToolKind kind = EToolKind.None;
	[Export(PropertyHint.Range, "0,100,0.5,or_greater")] public float wear = 1f;
}
