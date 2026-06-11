using Godot;

[GlobalClass]
public partial class TorchData : ConsumableData
{
	// MovingLight scene attached to the player while this torch is the active
	// consumable and isActive is true. The scene's exported Emission / LightColor
	// are the torch's brightness — author per-torch variants by duplicating the
	// scene rather than overriding here.
	[Export] public PackedScene movingLightScene;

	// One-shot steam/sizzle cue spawned on the player when this torch is doused
	// by the environment (heavy rain / swimming) rather than snuffed by hand.
	// Layers on top of the MovingLight's normal off-cue so a water douse reads
	// distinctly wet. Optional — null falls back to just the off-cue.
	[Export] public PackedScene douseEffectScene;

	public override ItemState CreateState()
	{
		return new TorchState(this);
	}
}
