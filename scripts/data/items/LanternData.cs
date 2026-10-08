using Godot;

// The carried lantern: a light source hung on the belt (the hotbar row), lit
// by hand and burning a fuel budget. At most one is lit (Inventory.LitLantern),
// and it goes out if it leaves the belt. It drives its tap (put
// it out) and fuel-costed actions through an ItemActionProfile like a spell or
// weapon does, but is otherwise its own item kind — it runs as a LanternState
// (fuel) and is NOT a spell or a pickup consumable.
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

	// How long (seconds of lit time) the lantern may burn before its fuel is
	// spent — it then goes out and can't be relit until refilled at a
	// campfire, a fountain or with oil. Only counts down while lit, on the
	// sim clock. 0 (or less) = burns forever.
	[Export] public float burnTimeSeconds = 0f;

	public bool HasLimitedFuel => burnTimeSeconds > 0f;
	public long BurnTimeMs => (long)(burnTimeSeconds * 1000f);

	// Not equippable: a lantern stays in its belt slot and is lit in place.
	protected override EItemCategory ComputeCategory() => EItemCategory.Lantern;

	public override ItemState CreateState()
	{
		return new LanternState(this);
	}
}
