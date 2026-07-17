using Godot;

[GlobalClass]
public partial class LanternData : ConsumableData
{
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

	// Lanterns live in the dedicated Lantern slot, never the Equipment hotbar —
	// so they carry their own category rather than inheriting ConsumableData's
	// Equipment. EquipSlotKind maps this straight to EInventorySlot.Lantern.
	protected override EItemCategory ComputeCategory() => EItemCategory.Lantern;

	public override ItemState CreateState()
	{
		return new LanternState(this);
	}
}
