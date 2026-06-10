using Godot;

[GlobalClass]
public partial class TorchData : ConsumableData
{
	// MovingLight scene attached to the player while this torch is the active
	// consumable and isActive is true. The scene's exported Emission / LightColor
	// are the torch's brightness — author per-torch variants by duplicating the
	// scene rather than overriding here.
	[Export] public PackedScene movingLightScene;

	// The visible in-hand torch prop (a HeldTorch scene) shown in the player's
	// hand while this torch is the active consumable. Lit/unlit visual and the
	// flame fx are driven by isActive. Distinct from movingLightScene, which is
	// the invisible world-light deposit. Shared with the mob held torch.
	[Export] public PackedScene heldTorchScene;

	public override ItemState CreateState()
	{
		return new TorchState(this);
	}
}
