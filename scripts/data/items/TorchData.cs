using Godot;

[GlobalClass]
public partial class TorchData : ConsumableData
{
	// One-shot steam/sizzle cue spawned on the player when this torch is doused
	// by the environment (heavy rain / swimming) rather than snuffed by hand.
	// Layers on top of the torch light's normal off-cue so a water douse reads
	// distinctly wet. Optional — null falls back to just the off-cue.
	[Export] public PackedScene douseEffectScene;
	// The visible torch prop (a HeldTorch scene) shown while this torch is carried
	// lit. The HeldTorch scene also carries its own world light (its
	// movingLightScene), so this one reference brings both the prop and its light.
	// Lit/unlit visual, flame fx, and the light are driven by isActive. Shared
	// with the mob held torch.
	[Export] public PackedScene heldTorchScene;

	// How long (seconds of lit time) this torch may burn before its fuel is
	// spent — it then extinguishes and refuses to relight until recharged at a
	// campfire (Player.RefuelCarriedTorches). Only counts down while lit, on the
	// sim clock. 0 (or less) = burns forever, the old always-on behavior.
	[Export] public float burnTimeSeconds = 0f;

	public bool HasLimitedFuel => burnTimeSeconds > 0f;
	public long BurnTimeMs => (long)(burnTimeSeconds * 1000f);

	// Lanterns live in the dedicated Lantern slot, never the Equipment hotbar —
	// so they carry their own category rather than inheriting ConsumableData's
	// Equipment. EquipSlotKind maps this straight to EInventorySlot.Lantern.
	protected override EItemCategory ComputeCategory() => EItemCategory.Lantern;

	public override ItemState CreateState()
	{
		return new TorchState(this);
	}
}
