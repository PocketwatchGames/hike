using Godot;

// A torch carried on the belt; torches don't stack. Use lights it and puts it out
// again; it lights only on the belt and goes out if it leaves. While lit it burns
// its durability (maxDurability, in lit seconds), and at zero it is destroyed.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class TorchData : CarriedLightData
{
	// Lands on the belt beside the potions it is used like.
	protected override EItemCategory ComputeCategory() => EItemCategory.Usable;

	public override ItemState CreateState()
	{
		return new TorchState(this);
	}
}
