using Godot;

// Floating world-space damage / heal / info number. Per-type scenes (one per
// EHudTextType) bake color, fade duration, and vertical movement as exports
// so the GameClient call site only passes runtime data (world position +
// text). GameClient picks which scene to instance from EHudTextType; the
// scene's exported fields decide how the number reads.
public partial class HudText : Control
{
	[Export] public Label label;

	// Color the label tints to. Faded out toward fadeDurationMs along an
	// ease-in (t²) curve so the number lingers near full opacity and snaps
	// out at the end rather than ramping linearly.
	[Export] public Color color = Colors.White;

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
		label.Modulate = color;
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

	// Called every frame. 'delta' is the elapsed time since the previous frame.
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
		label.Modulate = new Color(color.R, color.G, color.B, 1.0f - Mathf.Pow(t, 2));
	}
}
