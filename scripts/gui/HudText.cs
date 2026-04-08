using Godot;
using System;

public partial class HudText : Control
{
	[Export] public Label label;

	World _world;
	Camera3D _camera;
	Vector3 _worldPosition;
	ulong _fadeEndGameTimeMs;
	ulong _fadeDurationMs;
	float _verticalMovement;


	public static void Create(PackedScene scene, World world, Camera3D camera, Vector3 worldPosition, string text, ulong fadeMs, float verticalMovement, Color color, Node parent)
	{
		var hudText = scene.Instantiate<HudText>();
		hudText.Init(world, camera, worldPosition, text, fadeMs, verticalMovement, color, parent);
	}

	void Init(World world, Camera3D camera, Vector3 worldPosition, string text, ulong fadeMs, float verticalMovement, Color color, Node parent)
	{
		_world = world;
		_camera = camera;
		_worldPosition = worldPosition;
		label.Text = text;
		label.Modulate = color;
		_verticalMovement = verticalMovement;
		_fadeDurationMs = fadeMs;
		_fadeEndGameTimeMs = _fadeDurationMs + _world.GameTimeMs;
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
		Vector2 screenPos = _camera.UnprojectPosition(_worldPosition);
		Position = screenPos + new Vector2(0, -_verticalMovement * Mathf.Pow(t, 2));
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
		float t = 1.0f - (float)(_fadeEndGameTimeMs - timeMs) / _fadeDurationMs;
		UpdateScreenPosition(t);
		label.Modulate = new Color(label.Modulate.R, label.Modulate.G, label.Modulate.B, 1.0f - Mathf.Pow(t, 2));
	}
}
