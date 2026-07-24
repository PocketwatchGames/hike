using Godot;

// The carried lantern: an equipped light source in the dedicated Lantern slot,
// toggled lit/unlit and burning a fuel budget. It drives its toggle (and a
// fuel-costed) action through an ItemActionProfile like a spell or weapon does,
// but is otherwise its own item kind — it runs as a LanternState (isActive +
// fuel) and is NOT a spell or a pickup consumable.
[GlobalClass]
public partial class LanternData : ItemData, IUsableItem
{
	// The lantern's toggle / fuel-costed action timeline.
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;

	// Shown in place of inventorySprite while the lantern is lit
	// (LanternState.isActive). Null falls back to inventorySprite.
	[Export] public Texture2D activeSprite;

	// One-shot steam/sizzle cue spawned on the player when the lantern is doused
	// by the environment (heavy rain / swimming) rather than snuffed by hand.
	// Layers on top of the lantern light's normal off-cue so a water douse reads
	// distinctly wet. Optional — null falls back to just the off-cue.
	[Export] public PackedScene douseEffectScene;
	// The visible lantern prop (a HeldTorch scene) shown while the lantern is
	// carried lit. The scene also carries its own world light (its
	// movingLightScene), so this one reference brings both the prop and its light.
	// Lit/unlit visual, flame fx, and the light are driven by isActive.
	[Export] public PackedScene heldLanternScene;

	// How long (seconds of lit time) the lantern may burn before its fuel is
	// spent — it then extinguishes and refuses to relight until recharged at a
	// sunrise, on respawn, or at a fountain (Player.RefuelLantern). Only counts
	// down while lit, on the sim clock. 0 (or less) = burns forever.
	[Export] public float burnTimeSeconds = 0f;

	public bool HasLimitedFuel => burnTimeSeconds > 0f;
	public long BurnTimeMs => (long)(burnTimeSeconds * 1000f);

	// Lanterns live in the dedicated Lantern slot, never the Equipment/spell
	// slot. EquipSlotKind maps this straight to EInventorySlot.Lantern.
	protected override EItemCategory ComputeCategory() => EItemCategory.Lantern;

	public override ItemState CreateState()
	{
		return new LanternState(this);
	}
}
