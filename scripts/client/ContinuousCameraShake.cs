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

	// Captured at _Ready when the parent is an Fx so we can disconnect in
	// _ExitTree even if reparenting / scene-tree teardown has invalidated
	// GetParent() by then. Null when parented elsewhere.
	private Fx _parentFx;

	public override void _Ready()
	{
		GameCamera.Current?.Shake?.RegisterContinuous(this);
		// Parented to an Fx loop scene? Bail the instant the owner calls
		// Stop() rather than waiting for the trailing particle wind-down
		// to free the Fx node (which can be seconds — see fire_column_loop's
		// 2.2s smoke lifetime).
		if (GetParent() is Fx fx)
		{
			_parentFx = fx;
			_parentFx.OnStopping += OnFxStopping;
		}
	}

	public override void _ExitTree()
	{
		GameCamera.Current?.Shake?.UnregisterContinuous(this);
		if (_parentFx != null && GodotObject.IsInstanceValid(_parentFx))
		{
			_parentFx.OnStopping -= OnFxStopping;
			_parentFx = null;
		}
	}

	private void OnFxStopping()
	{
		QueueFree();
	}
}
