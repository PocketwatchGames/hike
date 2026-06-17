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

	public override ItemState CreateState()
	{
		return new TorchState(this);
	}
}
