using Godot;

// Floating world-space damage / heal / info number. Per-type scenes (one per
// EHudTextType) bake fade duration and vertical movement as exports; color
// is authored directly on the Label inside each scene and cascades through
// the Control's Modulate untouched. GameClient picks which scene to instance
// from EHudTextType; this script only owns the alpha fade + upward drift.
public partial class HudText : Control
{
	[Export] public Label label;

	// Total lifetime in ms. Drives both the alpha fade and the upward drift.
	[Export] public ulong fadeDurationMs = 1000;

	// Total pixels the number drifts upward over its lifetime, eased on t².
	[Export] public float verticalMovement = 32f;

	World _world;
	Camera3D _camera;
	Vector3 _worldPosition;
	ulong _fadeEndGameTimeMs;


	public static void Create(PackedScene scene, World world, Camera3D camera, Vector3 worldPosition, string text, Node parent)
	{
		var hudText = scene.Instantiate<HudText>();
		hudText.Init(world, camera, worldPosition, text, parent);
	}

	void Init(World world, Camera3D camera, Vector3 worldPosition, string text, Node parent)
	{
		_world = world;
		_camera = camera;
		_worldPosition = worldPosition;
		label.Text = text;
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
		Position = screenPos + new Vector2(0, -verticalMovement * Mathf.Pow(t, 2));
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
		// Touch only the alpha — the Label's authored color (via Modulate or
		// theme override) cascades through the Control's Modulate, so writing
		// here would multiply against it. Pulling the current modulate and
		// substituting alpha preserves whatever the scene authored.
		Modulate = new Color(Modulate.R, Modulate.G, Modulate.B, 1.0f - Mathf.Pow(t, 2));
	}
}
