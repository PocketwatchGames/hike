using Godot;

// Speech bubble HUD anchored to a Mob's HudAnchor. Spawned when an
// InteractiveAction with verb=Talk completes — see Mob.SpeakChatter and
// GameClient.OnMobChatterRequested. Tracks the mob in screen-space each
// frame so the bubble follows a moving NPC, then fades out and frees itself
// after the authored duration. Distinct from HudText (one-shot floating
// damage / pickup readout) because chatter needs to stay anchored to a
// specific actor for its full duration rather than projecting onto a
// world-fixed position.
//
// The .tscn for this scene is authored by the user; this script wires up
// the typical Label child via [Export]. Auto-frees on the mob exiting the
// tree so a dialogue line never outlives its speaker.
public partial class MobChatter : Node2D
{
	[Export] private Label _label;

	private Camera3D _camera;
	private Mob _mob;
	private ulong _fadeEndGameTimeMs;
	private ulong _fadeDurationMs;
	private Color _baseColor;

	public static void Create(PackedScene scene, Camera3D camera, Mob mob, string text, ulong durationMs, Node parent)
	{
		if (scene == null || mob == null)
		{
			return;
		}
		var bubble = scene.Instantiate<MobChatter>();
		bubble.Init(camera, mob, text, durationMs, parent);
	}

	private void Init(Camera3D camera, Mob mob, string text, ulong durationMs, Node parent)
	{
		_camera = camera;
		_mob = mob;
		if (_label != null)
		{
			_label.Text = text;
			_baseColor = _label.Modulate;
		}
		_fadeDurationMs = durationMs;
		_fadeEndGameTimeMs = (mob.World?.GameTimeMs ?? 0) + durationMs;
		_mob.TreeExiting += QueueFree;
		if (parent != null)
		{
			parent.AddChild(this);
		}
		UpdatePosition();
	}

	private void UpdatePosition()
	{
		Vector3 worldPosition = _mob.HudAnchor != null ? _mob.HudAnchor.GlobalPosition : _mob.GlobalPosition;
		if (_camera.IsPositionBehind(worldPosition))
		{
			Visible = false;
			return;
		}
		Visible = true;
		Position = GameClient.Current.ProjectToScreen(worldPosition);
	}

	public override void _Process(double delta)
	{
		ulong now = _mob?.World?.GameTimeMs ?? 0;
		if (now >= _fadeEndGameTimeMs)
		{
			QueueFree();
			return;
		}
		UpdatePosition();
		if (_label != null && _fadeDurationMs > 0)
		{
			// Hold full opacity for the first half, then linearly fade so the
			// reader has time to register the line before it starts dropping.
			ulong remaining = _fadeEndGameTimeMs - now;
			float alpha = Mathf.Clamp(remaining * 2f / _fadeDurationMs, 0f, 1f);
			_label.Modulate = new Color(_baseColor.R, _baseColor.G, _baseColor.B, _baseColor.A * alpha);
		}
	}
}
