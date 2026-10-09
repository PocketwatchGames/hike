using Godot;

// The carried lantern: a light source worn in the lantern equip slot (the
// first hotbar entry), lit by hand and burning the carrier's lantern oil
// (Player.LanternOil). Only the lantern-slot one can be lit
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

	// One-shot steam/sizzle cue spawned on the player when the lantern is doused
	// by the environment (heavy rain / swimming) rather than snuffed by hand.
	// Layers on top of the lantern light's normal off-cue so a water douse reads
	// distinctly wet. Optional — null falls back to just the off-cue.
	[Export] public PackedScene douseEffectScene;
	// The visible lantern prop (a HeldTorch scene) shown while the lantern is
	// lit. The scene also carries its own world light (its
	// movingLightScene), so this one reference brings both the prop and its light.
	[Export] public PackedScene heldLanternScene;

	// Seconds of lit time this lantern takes to burn 1 oil (Player.LanternOil;
	// a default supply, PlayerData.maxLanternOil, is 100). Empty puts it out, and it can't be relit
	// until refilled at a campfire, a fountain or with oil.
	[Export(PropertyHint.Range, "0.1,60,0.1,or_greater")] public float secondsPerOil = 3.6f;

	// Equips into EInventorySlot.Lantern; a spare is carried like any other gear.
	protected override EItemCategory ComputeCategory() => EItemCategory.Lantern;

	public override ItemState CreateState()
	{
		return new LanternState(this);
	}
}
