using Godot;

// An item the player carries lit: the lantern (in its equip slot) or a torch (on
// the belt). At most one burns at a time (Inventory.LitLight), and it goes out
// when it leaves the place it can burn in. Rain past PlayerData
// .lanternDouseRainThreshold and swimming douse either kind. Gear, so a torch's
// burn is durability; the lantern never wears (maxDurability 0).
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class CarriedLightData : GearData
{
	// Shown in place of inventorySprite while lit. Null falls back to
	// inventorySprite.
	[Export] public Texture2D activeSprite;

	public override Texture2D SlotIcon(bool active) => active && activeSprite != null ? activeSprite : inventorySprite;

	// One-shot cues for the player lighting it and snuffing it by hand. Only the
	// deliberate act plays them — not a load, a swap or a sleep.
	[Export] public PackedScene lightEffect;
	[Export] public PackedScene extinguishEffect;
	// One-shot steam/sizzle cue spawned on the player when it is doused by the
	// environment (heavy rain / swimming) or burns out, rather than snuffed by hand.
	[Export] public PackedScene douseEffectScene;
	// The visible prop (a HeldTorch scene) shown clipped to the belt while lit.
	// The scene carries its own world light (its movingLightScene), so this one
	// reference brings both the prop and its light.
	[Export] public PackedScene heldScene;
}
