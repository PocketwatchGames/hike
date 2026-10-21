using Godot;

// The carried lantern: a light source worn in the lantern equip slot (the
// first hotbar entry), lit by hand and burning the oil it holds (LanternState.oil,
// backed by the carrier's oil flasks — Player.LanternOil). Only the lantern-slot one can be lit
// (Inventory.LitLantern), and it goes out if it leaves the slot. It
// drives its tap (put it out) and oil-costed actions through an
// ItemActionProfile like a spell or weapon does, but is otherwise its own item
// kind and is NOT a spell or a pickup consumable.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class LanternData : ItemData, IUsableItem
{
	// The lantern's put-out / fuel-costed action timeline.
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;

	// Shown in place of inventorySprite while the lantern is lit.
	// Null falls back to inventorySprite.
	[Export] public Texture2D activeSprite;

	public override Texture2D SlotIcon(bool active) => active && activeSprite != null ? activeSprite : inventorySprite;

	// One-shot cues for the player lighting the lantern and snuffing it by hand.
	// Only the deliberate act plays them — not a load, a swap or a sleep.
	[Export] public PackedScene lightEffect;
	[Export] public PackedScene extinguishEffect;
	// One-shot steam/sizzle cue spawned on the player when the lantern is doused
	// by the environment (heavy rain / swimming) rather than snuffed by hand.
	[Export] public PackedScene douseEffectScene;
	// The visible lantern prop (a HeldTorch scene) shown while the lantern is
	// lit. The scene also carries its own world light (its
	// movingLightScene), so this one reference brings both the prop and its light.
	[Export] public PackedScene heldLanternScene;

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
