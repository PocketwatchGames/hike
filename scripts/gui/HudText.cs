using Godot;
using System;

public partial class HudText : Control
{
	[Export] public Label label;

	Camera3D _camera;
	Vector3 _worldPosition;
	ulong _fadeMs;
	ulong _fadeDurationMs;
	float _verticalMovement;


	public static void Create(PackedScene scene, Camera3D camera, Vector3 worldPosition, string text, ulong fadeMs, float verticalMovement, Color color, Node parent)
	{
		var hudText = scene.Instantiate<HudText>();
		hudText.Init(camera, worldPosition, text, fadeMs, verticalMovement, color, parent);
	}

	void Init(Camera3D camera, Vector3 worldPosition, string text, ulong fadeMs, float verticalMovement, Color color, Node parent)
	{
		_camera = camera;
		_worldPosition = worldPosition;
		label.Text = text;
		label.Modulate = color;
		_verticalMovement = verticalMovement;
		_fadeDurationMs = fadeMs;
		_fadeMs = _fadeDurationMs + Time.GetTicksMsec();
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
		ulong timeMs = Time.GetTicksMsec();
		if (timeMs >= _fadeMs)
		{
			QueueFree();
			return;
		}
		float t = 1.0f - (float)(_fadeMs - timeMs) / _fadeDurationMs;
		UpdateScreenPosition(t);
		label.Modulate = new Color(label.Modulate.R, label.Modulate.G, label.Modulate.B, 1.0f - Mathf.Pow(t, 2));
	}
}
