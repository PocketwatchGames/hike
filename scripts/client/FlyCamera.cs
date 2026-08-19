using Godot;

// Debug free-fly camera. Detaches the GameCamera from the player and drives
// it with WASD / Space / Ctrl movement + right-drag mouse look while the
// `debugFlyCam` CVar is on. Purely a development tool — GameClient owns the
// gating: it ticks this in _Process while the CVar is enabled, forwards
// mouse-motion from _Input, and calls Reset() whenever the fly cam is not the
// active camera mode so re-enabling it doesn't snap from a stale orientation.
[GlobalClass]
public partial class FlyCamera : Node
{
	[Export] public GameCamera camera;

	// Movement speed in m/s at the base (un-boosted) rate.
	[Export(PropertyHint.Range, "1,200,1")] public float moveSpeed = 20f;
	// Hold-Shift speed multiplier.
	[Export(PropertyHint.Range, "1,20,0.5")] public float boostMultiplier = 5f;
	// Radians of look rotation per pixel of right-drag mouse motion.
	[Export(PropertyHint.Range, "0.0005,0.05,0.0005")] public float lookSensitivity = 0.005f;

	float _yaw;
	float _pitch;
	bool _initialized;

	// Re-seed pitch/yaw from the camera's current orientation the next time the
	// fly camera takes over. Called by GameClient whenever the fly cam is not
	// the active camera mode so re-enabling it picks up the live framing.
	public void Reset()
	{
		_initialized = false;
	}

	// Per-frame drive. Called from GameClient._Process only while the
	// `debugFlyCam` CVar is enabled.
	public void Tick(double deltaTime)
	{
		if (camera == null) { return; }
		if (!_initialized)
		{
			Vector3 rot = camera.GlobalRotation;
			_pitch = rot.X;
			_yaw = rot.Y;
			camera.SetClip(float.PositiveInfinity, camera.GlobalPosition, allowMaxClip: false);
			_initialized = true;
		}

		float dt = (float)deltaTime;
		Vector3 move = Vector3.Zero;
		if (Input.IsPhysicalKeyPressed(Key.W)) { move.Z -= 1f; }
		if (Input.IsPhysicalKeyPressed(Key.S)) { move.Z += 1f; }
		if (Input.IsPhysicalKeyPressed(Key.A)) { move.X -= 1f; }
		if (Input.IsPhysicalKeyPressed(Key.D)) { move.X += 1f; }
		if (Input.IsPhysicalKeyPressed(Key.Space)) { move.Y += 1f; }
		if (Input.IsPhysicalKeyPressed(Key.Ctrl)) { move.Y -= 1f; }

		float speed = moveSpeed;
		if (Input.IsPhysicalKeyPressed(Key.Shift)) { speed *= boostMultiplier; }

		camera.GlobalRotation = new Vector3(_pitch, _yaw, 0);
		if (move.LengthSquared() > 0f)
		{
			Basis basis = camera.GlobalBasis;
			Vector3 worldMove = (basis.X * move.X + basis.Z * move.Z) + Vector3.Up * move.Y;
			camera.GlobalPosition += worldMove.Normalized() * speed * dt;
		}
	}

	// Right-drag look. Returns true when the event was consumed (fly cam on
	// AND right mouse held) so GameClient can swallow it before the gameplay
	// aim handler runs.
	public bool HandleMouseMotion(InputEventMouseMotion motion)
	{
		if (!CVars.debugFlyCam.Value || !Input.IsMouseButtonPressed(MouseButton.Right))
		{
			return false;
		}
		_yaw -= motion.Relative.X * lookSensitivity;
		_pitch -= motion.Relative.Y * lookSensitivity;
		_pitch = Mathf.Clamp(_pitch, -Mathf.Pi / 2f + 0.01f, Mathf.Pi / 2f - 0.01f);
		return true;
	}
}
