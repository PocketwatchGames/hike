using Godot;

// Previews the traversal a Dash press would perform — an up arrow over a ledge
// or wall the player can climb, a down arrow over one they can drop from.
// Exactly one of the two is ever visible, because Player.TraversalPreview names
// a single direction (see ETraversalPreview); the HUD only draws what the press
// already decided.
//
// Placed like the InteractHUD: a Node2D under worldHUD, moved each frame to the
// projection of a world anchor (Player.TraversalPromptPosition, which the player
// smooths). Distinct from it in every other way — no actions, no hold bar, no
// options modal, nothing to interact WITH.
//
// Lifecycle: spawned and freed by GameClient.UpdateClimbHUD as the preview
// appears and clears.
public partial class ClimbHUD : Node2D
{
	[Export] private TextureRect _climbUpIcon;
	[Export] private TextureRect _climbDownIcon;

	// Opacity of the prompt. It is a suggestion, not a committed action, so it
	// sits below the InteractHUD's own full-strength committed state.
	[Export(PropertyHint.Range, "0,1,0.01")] private float _opacity = 0.5f;

	Camera3D _camera;
	Player _player;

	// Who this HUD is bound to, so control passing to another party member
	// rebuilds it instead of leaving it reading the outgoing member's preview.
	public Player Player => _player;

	public static ClimbHUD Create(PackedScene scene, Camera3D camera, Player player, Node parent)
	{
		var hud = scene.Instantiate<ClimbHUD>();
		hud.Init(camera, player, parent);
		return hud;
	}

	void Init(Camera3D camera, Player player, Node parent)
	{
		_camera = camera;
		_player = player;
		_player.TreeExiting += QueueFree;
		if (parent != null)
		{
			parent.AddChild(this);
		}
		Update();
	}

	public override void _Process(double delta)
	{
		using var _prof = Profiler.Sample("ClimbHUD.Process");

		Update();
	}

	void Update()
	{
		ETraversalPreview preview = _player.TraversalPreview;
		// Hidden rather than freed while a fullscreen HUD (merchant, conversation,
		// cooking, ...) holds input: the affordance is still there, the player just
		// isn't in the world to take it. GameClient frees us when it actually goes
		// away.
		GameClient gc = GameClient.Current;
		Vector3 worldPosition = _player.TraversalPromptPosition;
		if (preview == ETraversalPreview.None
			|| (gc != null && gc.InputSuppressed)
			|| _camera.IsPositionBehind(worldPosition))
		{
			Visible = false;
			return;
		}

		Visible = true;
		Position = gc?.ProjectToScreen(worldPosition) ?? Vector2.Zero;
		Modulate = new Color(1f, 1f, 1f, _opacity);

		// The two icons share a slot in the scene, so they must never both be up.
		if (_climbUpIcon != null)
		{
			_climbUpIcon.Visible = preview == ETraversalPreview.Up;
		}
		if (_climbDownIcon != null)
		{
			_climbDownIcon.Visible = preview == ETraversalPreview.Down;
		}
	}
}
