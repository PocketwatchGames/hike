using Godot;

// The carried lantern: a light source worn in the lantern equip slot (the
// first hotbar entry), lit by hand and burning the oil it holds (LanternState.oil,
// backed by the carrier's oil flasks — Player.LanternOil). Only the lantern-slot one can be lit
// (Inventory.LitLight), and it goes out if it leaves the slot. It
// drives its tap (put it out) and oil-costed actions through an
// ItemActionProfile like a spell or weapon does, but is otherwise its own item
// kind and is NOT a spell or a pickup consumable.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class LanternData : CarriedLightData, IUsableItem
{
	// The lantern's put-out / fuel-costed action timeline.
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;

	// Seconds of lit time this lantern takes to burn 1 oil (Player.LanternOil).
	// Empty puts it out, and it can't be relit until refilled at an oil
	// fountain or with oil.
	[Export(PropertyHint.Range, "0.1,60,0.1,or_greater")] public float secondsPerOil = 3.6f;

	// Oil this lantern holds when full. Carried oil flasks add their capacity on
	// top (Player.MaxLanternOil). A new lantern comes full.
	[Export(PropertyHint.Range, "1,1000,1")] public float oilCapacity = 100f;

	// Equips into EInventorySlot.Lantern; a spare is carried like any other gear.
	protected override EItemCategory ComputeCategory() => EItemCategory.Lantern;

	public override ItemState CreateState()
	{
		return new LanternState(this);
	}
}
