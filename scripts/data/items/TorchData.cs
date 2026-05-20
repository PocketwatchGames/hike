using Godot;

[GlobalClass]
public partial class TorchData : ConsumableData
{
	// MovingLight scene attached to the player while this torch is the active
	// consumable and isActive is true. The scene's exported Emission / LightColor
	// are the torch's brightness — author per-torch variants by duplicating the
	// scene rather than overriding here.
	[Export] public PackedScene movingLightScene;

	public override ItemState CreateState()
	{
		return new TorchState(this);
	}
}
