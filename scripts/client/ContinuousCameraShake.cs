using Godot;

// Drop-in Node3D that registers itself with the active GameCamera's shake
// driver for as long as it exists in the scene tree. Author as a child of
// any environmental hazard (rain-of-arrows AoE, earthquake trap, large
// engine) that should rumble the screen while present. Magnitude is in
// meters of camera offset at zero distance; range is the world-space radius
// beyond which the contribution falls to zero. range == 0 disables falloff.
[GlobalClass]
public partial class ContinuousCameraShake : Node3D
{
	[Export] public float magnitude = 0.1f;
	[Export] public float range = 8f;

	public override void _Ready()
	{
		GameCamera.Current?.Shake?.RegisterContinuous(this);
	}

	public override void _ExitTree()
	{
		GameCamera.Current?.Shake?.UnregisterContinuous(this);
	}
}
