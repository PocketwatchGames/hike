using Godot;
using System;

public partial class InteractHUD : Node2D
{
	[Export] private ProgressBar _interactTimer;

	Camera3D _camera;
	Player _player;
	IInteractive _interactive;

	public static InteractHUD Create(PackedScene scene, Camera3D camera, Player player, IInteractive interactive, Node parent)
	{
		var hud = scene.Instantiate<InteractHUD>();
		hud.Init(camera, player, interactive, parent);
		return hud;
	}

	void Init(Camera3D camera, Player player, IInteractive interactive, Node parent)
	{
		_camera = camera;
		_player = player;
		_interactive = interactive;
		_player.TreeExiting += QueueFree;
		if (parent != null)
		{
			parent.AddChild(this);
		}
		Update();
	}

	void Update()
	{
		Vector3 worldPosition = _interactive.hudPosition;
		if (_camera.IsPositionBehind(worldPosition))
		{
			Visible = false;
			return;
		}

		Visible = true;
		Position = GameClient.Current.ProjectToScreen(worldPosition);
		if (_interactTimer != null)
		{
			_interactTimer.Value = _player.ClientInteractProgress;
		}
	}

	public override void _Process(double delta)
	{
		Update();
	}
}
