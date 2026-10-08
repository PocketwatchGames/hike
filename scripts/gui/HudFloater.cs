using Godot;

// A world-anchored HUD element that drifts up and fades out, then frees itself.
// Subclasses supply the content (HudText's number, HudItemIcon's sprite);
// colour and size are authored in each scene and cascade through Modulate,
// whose alpha is the only channel touched here.
[GlobalClass]
public partial class HudFloater : Control
{
	// Total lifetime in ms. Drives both the alpha fade and the upward drift.
	[Export] public ulong fadeDurationMs = 1000;

	// Total pixels the element drifts upward over its lifetime, eased on t².
	[Export] public float verticalMovement = 32f;

	Sim _world;
	Camera3D _camera;
	Vector3 _worldPosition;
	// Fixed screen-space nudge from the projected anchor — spreads several
	// floaters spawned at one point.
	Vector2 _screenOffset;
	ulong _fadeEndGameTimeMs;

	protected void Init(Sim sim, Camera3D camera, Vector3 worldPosition, Vector2 screenOffset, Node parent)
	{
		_world = sim;
		_camera = camera;
		_worldPosition = worldPosition;
		_screenOffset = screenOffset;
		_fadeEndGameTimeMs = fadeDurationMs + _world.GameTimeMs;
		if (parent != null)
		{
			parent.AddChild(this);
		}
		UpdateScreenPosition(0);
	}

	void UpdateScreenPosition(float t)
	{
		if (_camera.IsPositionBehind(_worldPosition))
		{
			Visible = false;
			return;
		}
		Visible = true;
		Vector2 screenPos = GameClient.Current.ProjectToScreen(_worldPosition);
		Position = screenPos + _screenOffset + new Vector2(0, -verticalMovement * Mathf.Pow(t, 2));
	}

	public override void _Process(double delta)
	{
		ulong timeMs = _world.GameTimeMs;
		if (timeMs >= _fadeEndGameTimeMs)
		{
			QueueFree();
			return;
		}
		float t = 1.0f - (float)(_fadeEndGameTimeMs - timeMs) / fadeDurationMs;
		UpdateScreenPosition(t);
		Modulate = new Color(Modulate.R, Modulate.G, Modulate.B, 1.0f - Mathf.Pow(t, 2));
	}
}
